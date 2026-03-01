//
// MidiJack - MIDI Input Plugin for Unity
//
// Copyright (C) 2013-2016 Keijiro Takahashi
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
//
using UnityEngine;
using UnityEditor;
using System.Runtime.InteropServices;

namespace MidiJack
{
    class MidiJackWindow : EditorWindow
    {
        #region Custom Editor Window Code

        [MenuItem("Window/MIDI Jack")]
        public static void ShowWindow()
        {
            EditorWindow.GetWindow<MidiJackWindow>("MIDI Jack");
        }

        void OnGUI()
        {
            var deviceCount = WindowsMidiInterop.NativeMethods.midiInGetNumDevs();

            // Endpoints
            var temp = "Detected MIDI devices:";
            for (uint i = 0; i < deviceCount; i++)
            {
                var name = GetDeviceName(i);
                temp += "\n" + i.ToString("X8") + ": " + name;
            }
            EditorGUILayout.HelpBox(temp, MessageType.None);

            // Message history
            temp = "Recent MIDI messages:";
            foreach (var message in MidiDriver.Instance.History)
                temp += "\n" + message.ToString();
            EditorGUILayout.HelpBox(temp, MessageType.None);
        }

        #endregion

        #region Update And Repaint

        const int _updateInterval = 15;
        int _countToUpdate;
        int _lastMessageCount;

        void Update()
        {
            if (--_countToUpdate > 0) return;

            var mcount = MidiDriver.Instance.TotalMessageCount;
            if (mcount != _lastMessageCount) {
                Repaint();
                _lastMessageCount = mcount;
            }

            _countToUpdate = _updateInterval;
        }

        #endregion

        #region Device Info

        static string GetDeviceName(uint deviceId)
        {
            var caps = new WindowsMidiInterop.NativeMethods.MIDIINCAPS();
            uint size = (uint)Marshal.SizeOf<WindowsMidiInterop.NativeMethods.MIDIINCAPS>();
            if (WindowsMidiInterop.NativeMethods.midiInGetDevCaps(deviceId, ref caps, size) == WindowsMidiInterop.NativeMethods.MMSYSERR_NOERROR)
            {
                return caps.szPname;
            }
            return "(unknown)";
        }

        #endregion
    }
}
