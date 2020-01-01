# What is Changed from Original

## Preamble : why forked

In Windows environment,

* Want to replace native plugin by C# code, for extensibility.
* Want to support disable MIDI read when user have another MIDI-input-reading apps like DAW.

`WindowsMidiInterop.cs`

## Diff in README

System Requirements:

* Unity 2018 or later
* Windows only. No support for Mac 

Installation

* No difference.

API Reference 

* Additional API `WindowsMidiInterop.Instance.SetActive(bool enable)` can toggle MIDI read on / off.
    - Use this API when you want another application to use MIDI input.
    - This API still have limitation that it is "all or nothing" style.

Current Limitations

* NO support for OS X.
* About MIDI device conflict, you can have workaround of `WindowsMidiInterop.Instance.SetActive(bool enable)` API.

License

* No difference. You can use with original Keijiro Takahashi's MIT license.

