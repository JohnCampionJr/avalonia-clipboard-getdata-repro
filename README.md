# Avalonia clipboard GetData repro

On Windows, `IClipboard.TryGetTextAsync` returns `null`, the same as "no text on the clipboard", when another process has the clipboard open at the moment Avalonia calls `IDataObject.GetData`. The failure isn't retried or reported.

## Run it

You need Windows and the .NET 10 SDK.

```
dotnet run -c Release
```

To try another Avalonia version, add `-p:AvaloniaVersion=12.0.4`. The exit code is 1 when it reproduces.

## What it does

1. **Deterministic:** a second process opens the clipboard and holds it for 1.5 seconds. The app reads the clipboard during the hold and again after it, and also makes the same OLE calls directly to show what `GetData` returned.
2. **In practice:** a second process opens the clipboard for 15 ms after every change, the way clipboard managers and other listeners do. The app runs 300 write-then-read pairs with no delay and 300 with a 100 ms delay. The count varies by machine and run; experiment 1 is the reliable one.

## Output

Windows Server 2025, Avalonia 12.1.4, .NET 10.0.13:

```
1. Read while another process holds the clipboard open
   read with nothing holding it:  "text that is on the clipboard"
   read while it is held:         null  (took 0 ms)
   IsClipboardFormatAvailable:    True
   OleGetClipboard + GetData:     OleGetClipboard S_OK, QueryGetData 0x00000000, GetData 0x800401D0
   read after it is released:     "text that is on the clipboard"

2. 300 write-then-read pairs, with another process opening the clipboard for 15 ms after each change
   read straight after the write: 1 of 300 came back null
   read 100 ms after the write:   0 of 300 came back null

REPRODUCED
```

`0x800401D0` is `CLIPBRD_E_CANT_OPEN`. Avalonia 12.0.4 gives the same result for experiment 1, and 3 of 300 for experiment 2.
