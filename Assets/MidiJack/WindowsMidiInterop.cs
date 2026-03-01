using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace MidiJack
{
    /// <summary>
    /// ネイティブの(MidiJackで叩いてるのと同じ)MIDI APIをC# コードから叩くためのラッパークラスで、
    /// もともとMidiDriverが呼び出していた関数に「ストップ/スタート」が増えたやつ
    /// </summary>
    public class WindowsMidiInterop
    {
        private const int SysExBufferSize = 256;

        private readonly NativeMethods.MidiInProcDelegate _midiInProc;

        private static WindowsMidiInterop _instance = null;
        public static WindowsMidiInterop Instance
            => _instance ?? (_instance = new WindowsMidiInterop());

        public WindowsMidiInterop()
        {
            _midiInProc = MidiInProc;
        }

        private class DeviceState
        {
            public IntPtr Handle;
            public IntPtr SysExHeaderPtr;
            public IntPtr SysExBufferPtr;
        }

        //NOTE: message ulong style accords to original MidiJack native
        private readonly ConcurrentQueue<ulong> _midiMessageQueue = new ConcurrentQueue<ulong>();
        private readonly ConcurrentStack<IntPtr> _handleToClose = new ConcurrentStack<IntPtr>();
        private readonly ConcurrentQueue<(IntPtr handle, IntPtr headerPtr)> _sysExBufferToReAdd = new ConcurrentQueue<(IntPtr, IntPtr)>();
        private readonly Dictionary<uint, DeviceState> _activeHandles = new Dictionary<uint, DeviceState>();

        public bool IsActive { get; private set; } = false;

        /// <summary>
        /// デバイスの接続状態を更新し、SysExバッファの再投入を行う。
        /// フレームあたり1回、メッセージ処理ループの前に呼ぶこと。
        /// </summary>
        public void UpdateDevices()
        {
            if (!IsActive)
            {
                return;
            }

            RefreshDevices();
        }

        /// <summary>
        /// キューに溜まったMIDIメッセージを1つ取り出す。0が返ったらキューは空。
        /// </summary>
        /// <returns></returns>
        public ulong DequeueIncomingData()
        {
            if (!IsActive)
            {
                return 0;
            }

            return _midiMessageQueue.TryDequeue(out var msg) ? msg : 0;
        }

        /// <summary>
        /// MIDI入力の読み込みがアクティブか、そうでないかを選択します。
        /// </summary>
        /// <param name="active"></param>
        public void SetActive(bool active)
        {
            if (IsActive == active)
            {
                return;
            }
            IsActive = active;

            if (IsActive)
            {
                RefreshDevices();
            }
            else
            {
                CloseAllDevices();
                while (_midiMessageQueue.TryDequeue(out _))
                {
                    //do nothing: clear
                }
            }
        }

        private void RefreshDevices()
        {
            while (_handleToClose.TryPop(out var handle))
            {
                NativeMethods.midiInClose(handle);
                var state = RemoveHandleFromActive(handle);
                if (state != null)
                {
                    FreeSysExMemory(state);
                }
            }

            // SysExコールバックで返却されたバッファを再投入
            uint headerSize = (uint)Marshal.SizeOf<NativeMethods.MIDIHDR>();
            while (_sysExBufferToReAdd.TryDequeue(out var item))
            {
                NativeMethods.midiInAddBuffer(item.handle, item.headerPtr, headerSize);
            }

            OpenAllDevices();
        }

        private void CloseAllDevices()
        {
            // 全デバイスのMIDI入力を停止し、保留中バッファを返却させる
            foreach (var kvp in _activeHandles)
            {
                NativeMethods.midiInReset(kvp.Value.Handle);
            }

            // midiInResetにより発火したMIM_LONGDATAコールバックのキューを排出
            while (_sysExBufferToReAdd.TryDequeue(out _)) { }

            // SysExバッファの解放とデバイスクローズ
            foreach (var kvp in _activeHandles)
            {
                var state = kvp.Value;
                CleanupSysExBuffer(state);
                NativeMethods.midiInClose(state.Handle);
            }
            _activeHandles.Clear();

            while (_handleToClose.TryPop(out var h))
            {
                NativeMethods.midiInClose(h);
            }
        }

        private void OpenAllDevices()
        {
            uint deviceCount = NativeMethods.midiInGetNumDevs();
            for (uint i = 0; i < deviceCount; i++)
            {
                OpenDevice(i);
            }
        }

        private void OpenDevice(uint id)
        {
            if (_activeHandles.ContainsKey(id))
            {
                return;
            }

            uint err = NativeMethods.midiInOpen(out IntPtr handle, id, _midiInProc);
            if (err != NativeMethods.MMSYSERR_NOERROR)
            {
                return;
            }

            if (NativeMethods.midiInStart(handle) != NativeMethods.MMSYSERR_NOERROR)
            {
                NativeMethods.midiInClose(handle);
                return;
            }

            var state = new DeviceState { Handle = handle };
            PrepareSysExBuffer(state);
            _activeHandles[id] = state;
        }

        private void PrepareSysExBuffer(DeviceState state)
        {
            state.SysExBufferPtr = Marshal.AllocHGlobal(SysExBufferSize);
            state.SysExHeaderPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.MIDIHDR>());

            var header = new NativeMethods.MIDIHDR();
            header.lpData = state.SysExBufferPtr;
            header.dwBufferLength = SysExBufferSize;
            Marshal.StructureToPtr(header, state.SysExHeaderPtr, false);

            uint headerSize = (uint)Marshal.SizeOf<NativeMethods.MIDIHDR>();
            if (NativeMethods.midiInPrepareHeader(state.Handle, state.SysExHeaderPtr, headerSize) != NativeMethods.MMSYSERR_NOERROR)
            {
                FreeSysExMemory(state);
                return;
            }

            if (NativeMethods.midiInAddBuffer(state.Handle, state.SysExHeaderPtr, headerSize) != NativeMethods.MMSYSERR_NOERROR)
            {
                NativeMethods.midiInUnprepareHeader(state.Handle, state.SysExHeaderPtr, headerSize);
                FreeSysExMemory(state);
            }
        }

        private void CleanupSysExBuffer(DeviceState state)
        {
            if (state.SysExHeaderPtr == IntPtr.Zero)
            {
                return;
            }

            uint headerSize = (uint)Marshal.SizeOf<NativeMethods.MIDIHDR>();
            NativeMethods.midiInUnprepareHeader(state.Handle, state.SysExHeaderPtr, headerSize);
            FreeSysExMemory(state);
        }

        private void FreeSysExMemory(DeviceState state)
        {
            if (state.SysExHeaderPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(state.SysExHeaderPtr);
                state.SysExHeaderPtr = IntPtr.Zero;
            }
            if (state.SysExBufferPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(state.SysExBufferPtr);
                state.SysExBufferPtr = IntPtr.Zero;
            }
        }

        private DeviceState RemoveHandleFromActive(IntPtr handle)
        {
            uint? keyToRemove = null;
            DeviceState state = null;
            foreach (var kvp in _activeHandles)
            {
                if (kvp.Value.Handle == handle)
                {
                    keyToRemove = kvp.Key;
                    state = kvp.Value;
                    break;
                }
            }

            if (keyToRemove.HasValue)
            {
                _activeHandles.Remove(keyToRemove.Value);
            }

            return state;
        }

        private void MidiInProc(IntPtr hMidiIn, uint wMsg, IntPtr dwInstance, IntPtr dwParam1, IntPtr dwParam2)
        {
            if (wMsg == NativeMethods.MIM_DATA)
            {
                uint id = (uint)hMidiIn.ToInt32();
                uint raw = (uint)dwParam1.ToInt32();
                _midiMessageQueue.Enqueue(CreateMidiMessage(id, raw));
            }
            else if (wMsg == NativeMethods.MIM_LONGDATA || wMsg == NativeMethods.MIM_LONGERROR)
            {
                // SysEx受信(または受信エラー): メインスレッドでバッファを再投入するためキューに積む
                _sysExBufferToReAdd.Enqueue((hMidiIn, dwParam1));
            }
            else if (wMsg == NativeMethods.MIM_CLOSE)
            {
                _handleToClose.Push(hMidiIn);
            }
        }

        //note: this message encoding accords to original MidiJack
        private ulong CreateMidiMessage(uint id, uint raw)
        {
            byte status = (byte)(raw & 0xff);
            byte data1 = (byte)((raw >> 8) & 0xff);
            byte data2 = (byte)((raw >> 16) & 0xff);

            ulong result = id;
            result |= (ulong)status << 32;
            result |= (ulong)data1 << 40;
            result |= (ulong)data2 << 48;

            return result;
        }

        public static class NativeMethods
        {
            private const int CALLBACK_FUNCTION = 0x30000;

            [StructLayout(LayoutKind.Sequential)]
            public struct MIDIHDR
            {
                public IntPtr lpData;
                public uint dwBufferLength;
                public uint dwBytesRecorded;
                public IntPtr dwUser;
                public uint dwFlags;
                public IntPtr lpNext;
                public IntPtr reserved;
                public uint dwOffset;
                public IntPtr dwReserved0, dwReserved1, dwReserved2, dwReserved3;
                public IntPtr dwReserved4, dwReserved5, dwReserved6, dwReserved7;
            }

            /// <summary>
            /// Callback function signature when received MIDI input.
            /// </summary>
            public delegate void MidiInProcDelegate(IntPtr hMidiIn, uint wMsg, IntPtr dwInstance, IntPtr dwParam1, IntPtr dwParam2);

            [DllImport("winmm.dll")]
            public static extern uint midiInGetNumDevs();

            [DllImport("winmm.dll")]
            public static extern uint midiInOpen(
                out IntPtr handle,
                uint id,
                MidiInProcDelegate callback,
                IntPtr hInstance,
                uint flags
                );

            public static uint midiInOpen(out IntPtr handle, uint id, MidiInProcDelegate callback)
                => midiInOpen(out handle, id, callback, IntPtr.Zero, CALLBACK_FUNCTION);

            [DllImport("winmm.dll")]
            public static extern uint midiInStart(IntPtr hMidiIn);

            [DllImport("winmm.dll")]
            public static extern uint midiInClose(IntPtr hMidiIn);

            [DllImport("winmm.dll")]
            public static extern uint midiInReset(IntPtr hMidiIn);

            [DllImport("winmm.dll")]
            public static extern uint midiInPrepareHeader(IntPtr hMidiIn, IntPtr lpMidiInHdr, uint cbMidiInHdr);

            [DllImport("winmm.dll")]
            public static extern uint midiInUnprepareHeader(IntPtr hMidiIn, IntPtr lpMidiInHdr, uint cbMidiInHdr);

            [DllImport("winmm.dll")]
            public static extern uint midiInAddBuffer(IntPtr hMidiIn, IntPtr lpMidiInHdr, uint cbMidiInHdr);

            public const int MMSYSERR_NOERROR = 0;
            public const int MIM_CLOSE = 0x3C2;
            public const int MIM_DATA = 0x3C3;
            public const int MIM_LONGDATA = 0x3C4;
            public const int MIM_LONGERROR = 0x3C5;
        }
    }
}
