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
        private readonly NativeMethods.MidiInProcDelegate _midiInProc;

        private static WindowsMidiInterop _instance = null;
        public static WindowsMidiInterop Instance 
            => _instance ?? (_instance = new WindowsMidiInterop());

        public WindowsMidiInterop()
        {
            _midiInProc = MidiInProc;
        }

        //NOTE: message ulong style accords to original MidiJack native
        private readonly ConcurrentQueue<ulong> _midiMessageQueue = new ConcurrentQueue<ulong>();
        private readonly ConcurrentStack<IntPtr> _handleToClose = new ConcurrentStack<IntPtr>();
        private readonly HashSet<IntPtr> _activeHandles = new HashSet<IntPtr>();

        public bool IsActive { get; private set; } = true;
        
        /// <summary>
        /// get queued message if exists.
        /// </summary>
        /// <returns></returns>
        public ulong DequeueIncomingData()
        {
            if (!IsActive)
            {
                return 0;
            }
            
            RefreshDevices();
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
                _activeHandles.Remove(handle);
            }

            OpenAllDevices();
        }

        private void CloseAllDevices()
        {
            foreach (var h in _activeHandles)
            {
                NativeMethods.midiInClose(h);
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
            uint err = NativeMethods.midiInOpen(out IntPtr handle, id, _midiInProc);
            if (err != NativeMethods.MMSYSERR_NOERROR)
            {
                return;
            }
            
            if (NativeMethods.midiInStart(handle) == NativeMethods.MMSYSERR_NOERROR)
            {
                _activeHandles.Add(handle);
            }
            else
            {
                NativeMethods.midiInClose(handle);
            }
        }

        private void MidiInProc(IntPtr hMidiIn, uint wMsg, IntPtr dwInstance, IntPtr dwParam1, IntPtr dwParam2)
        {
            if (wMsg == NativeMethods.MIM_DATA)
            {
                uint id = (uint)hMidiIn.ToInt32();
                uint raw = (uint)dwParam1.ToInt32();
                _midiMessageQueue.Enqueue(CreateMidiMessage(id, raw));
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
            
            /// <summary>
            /// Callback function signature when received MIDI input.
            /// </summary>
            /// <param name="hMidiIn"></param>
            /// <param name="wMsg"></param>
            /// <param name="dwInstance"></param>
            /// <param name="dwParam1"></param>
            /// <param name="dwParam2"></param>
            public delegate void MidiInProcDelegate(IntPtr hMidiIn, uint wMsg, IntPtr dwInstance, IntPtr dwParam1, IntPtr dwParam2);
            
            /// <summary>
            /// Get how many MIDI input devices available.
            /// </summary>
            /// <returns></returns>
            [DllImport("winmm.dll")]
            public static extern uint midiInGetNumDevs();

            /// <summary>
            /// Open MIDI input device if available.
            /// </summary>
            /// <param name="handle"></param>
            /// <param name="id"></param>
            /// <param name="callback"></param>
            /// <param name="hInstance"></param>
            /// <param name="flags"></param>
            /// <returns></returns>
            [DllImport("winmm.dll")]
            public static extern uint midiInOpen(
                out IntPtr handle, 
                uint id,
                MidiInProcDelegate callback,
                IntPtr hInstance,
                uint flags
                );

            
            /// <summary>
            /// Open MIDI input device if available.
            /// </summary>
            /// <param name="handle"></param>
            /// <param name="id"></param>
            /// <param name="callback"></param>
            /// <returns></returns>
            public static uint midiInOpen(out IntPtr handle, uint id, MidiInProcDelegate callback)
                => midiInOpen(out handle, id, callback, IntPtr.Zero, CALLBACK_FUNCTION);
            
            /// <summary>
            /// Start receiving MIDI input data.
            /// </summary>
            /// <param name="hMidiIn"></param>
            /// <returns></returns>
            [DllImport("winmm.dll")]
            public static extern uint midiInStart(IntPtr hMidiIn);

            /// <summary>
            /// End receiving MIDI input data and close.
            /// </summary>
            /// <param name="hMidiIn"></param>
            /// <returns></returns>
            [DllImport("winmm.dll")]
            public static extern uint midiInClose(IntPtr hMidiIn);
            
            public const int MMSYSERR_NOERROR = 0;
            public const int MIM_CLOSE = 0x3C2;
            public const int MIM_DATA = 0x3C3;
        } 
    }
}
