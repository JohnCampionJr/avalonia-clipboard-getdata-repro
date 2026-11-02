using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using ComTypes = System.Runtime.InteropServices.ComTypes;

// Shows that IClipboard.TryGetTextAsync returns null, as if there were no text, when another
// process has the clipboard open at the moment Avalonia calls IDataObject.GetData.
//
//   ClipboardGetDataRepro            run both experiments
//   ClipboardGetDataRepro --hold N   (internal) open the clipboard for N ms
//   ClipboardGetDataRepro --listen   (internal) open it briefly after every change, like a clipboard manager
internal static class Program
{
    private const uint CF_UNICODETEXT = 13;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("ole32.dll")] private static extern int OleGetClipboard(out IntPtr dataObject);
    [DllImport("ole32.dll")] private static extern void ReleaseStgMedium(ref ComTypes.STGMEDIUM medium);

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--hold")
            return Hold(int.Parse(args[1]));
        if (args.Length == 1 && args[0] == "--listen")
            return Listen();

        AppBuilder.Configure<Application>().UsePlatformDetect().SetupWithoutStarting();

        var finished = new CancellationTokenSource();
        var exitCode = 0;
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                exitCode = await RunAsync();
            }
            catch (Exception e)
            {
                Console.WriteLine($"Repro itself failed: {e}");
                exitCode = 2;
            }
            finished.Cancel();
        });
        Dispatcher.UIThread.MainLoop(finished.Token);
        return exitCode;
    }

    private static async Task<int> RunAsync()
    {
        var window = new Window { Title = "ClipboardGetDataRepro", Width = 320, Height = 120 };
        window.Show();
        await Task.Delay(300);
        var clipboard = window.Clipboard ?? throw new InvalidOperationException("no clipboard");

        Console.WriteLine($"Avalonia {typeof(Application).Assembly.GetName().Version}, {RuntimeInformation.RuntimeIdentifier}, {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine();

        var nulls = await WhileAnotherProcessHoldsItAsync(clipboard);
        Console.WriteLine();
        var misses = await StraightAfterAWriteAsync(clipboard);

        window.Close();
        Console.WriteLine();
        Console.WriteLine(nulls || misses > 0 ? "REPRODUCED" : "NOT REPRODUCED");
        return nulls || misses > 0 ? 1 : 0;
    }

    /// <summary>The deterministic version: nothing depends on timing except a generous hold.</summary>
    private static async Task<bool> WhileAnotherProcessHoldsItAsync(IClipboard clipboard)
    {
        Console.WriteLine("1. Read while another process holds the clipboard open");

        const string text = "text that is on the clipboard";
        await clipboard.SetTextAsync(text);
        await clipboard.FlushAsync();
        await Task.Delay(200);
        Console.WriteLine($"   read with nothing holding it:  {Describe(await ReadAsync(clipboard))}");

        using var holder = Spawn("--hold", "1500");
        if (holder.StandardOutput.ReadLine() != "held")
            throw new InvalidOperationException("the holder could not open the clipboard");

        var clock = Stopwatch.StartNew();
        var during = await ReadAsync(clipboard);
        var took = clock.ElapsedMilliseconds;
        Console.WriteLine($"   read while it is held:         {Describe(during)}  (took {took} ms)");
        Console.WriteLine($"   IsClipboardFormatAvailable:    {IsClipboardFormatAvailable(CF_UNICODETEXT)}");
        Console.WriteLine($"   OleGetClipboard + GetData:     {RawGetData()}");

        holder.WaitForExit();
        Console.WriteLine($"   read after it is released:     {Describe(await ReadAsync(clipboard))}");
        return during.Text == null && during.Thrown == null;
    }

    /// <summary>The way it is met in practice: something reacts to every clipboard change.</summary>
    private static async Task<int> StraightAfterAWriteAsync(IClipboard clipboard)
    {
        const int reads = 300;
        Console.WriteLine($"2. {reads} write-then-read pairs, with another process opening the clipboard for 15 ms after each change");

        using var listener = Spawn("--listen");
        listener.StandardOutput.ReadLine();
        try
        {
            var misses = await WriteThenReadAsync(clipboard, reads, delayMs: 0);
            Console.WriteLine($"   read straight after the write: {misses} of {reads} came back null");
            var delayed = await WriteThenReadAsync(clipboard, reads, delayMs: 100);
            Console.WriteLine($"   read 100 ms after the write:   {delayed} of {reads} came back null");
            return misses;
        }
        finally
        {
            listener.Kill();
        }
    }

    private static async Task<int> WriteThenReadAsync(IClipboard clipboard, int count, int delayMs)
    {
        var misses = 0;
        for (var i = 0; i < count; i++)
        {
            var expected = $"value {i}";
            await clipboard.SetTextAsync(expected);
            if (delayMs > 0)
                await Task.Delay(delayMs);

            var (text, _) = await ReadAsync(clipboard);
            if (text != expected)
                misses++;

            await Task.Delay(30);
        }
        return misses;
    }

    private static async Task<(string? Text, string? Thrown)> ReadAsync(IClipboard clipboard)
    {
        try
        {
            return (await clipboard.TryGetTextAsync(), null);
        }
        catch (Exception e)
        {
            return (null, $"{e.GetType().Name} 0x{e.HResult:X8}");
        }
    }

    private static string Describe((string? Text, string? Thrown) read)
        => read.Thrown != null ? $"threw {read.Thrown}" : read.Text == null ? "null" : $"\"{read.Text}\"";

    /// <summary>The same two calls Avalonia makes, without Avalonia, to show what GetData returned.</summary>
    private static string RawGetData()
    {
        var hr = OleGetClipboard(out var pointer);
        if (hr != 0)
            return $"OleGetClipboard returned 0x{hr:X8}";

        try
        {
            var dataObject = (ComTypes.IDataObject)Marshal.GetObjectForIUnknown(pointer);
            var format = new ComTypes.FORMATETC
            {
                cfFormat = (short)CF_UNICODETEXT,
                dwAspect = ComTypes.DVASPECT.DVASPECT_CONTENT,
                lindex = -1,
                tymed = ComTypes.TYMED.TYMED_HGLOBAL,
            };

            var query = dataObject.QueryGetData(ref format);
            try
            {
                dataObject.GetData(ref format, out var medium);
                ReleaseStgMedium(ref medium);
                return $"OleGetClipboard S_OK, QueryGetData 0x{query:X8}, GetData S_OK";
            }
            catch (COMException e)
            {
                return $"OleGetClipboard S_OK, QueryGetData 0x{query:X8}, GetData 0x{e.HResult:X8}";
            }
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    private static Process Spawn(params string[] arguments)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!) { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        return Process.Start(info)!;
    }

    private static int Hold(int milliseconds)
    {
        var clock = Stopwatch.StartNew();
        while (!OpenClipboard(IntPtr.Zero))
        {
            if (clock.ElapsedMilliseconds > 5000)
                throw new Win32Exception();
            Thread.Sleep(10);
        }

        Console.WriteLine("held");
        Console.Out.Flush();
        Thread.Sleep(milliseconds);
        CloseClipboard();
        return 0;
    }

    private static int Listen()
    {
        Console.WriteLine("listening");
        Console.Out.Flush();
        var seen = GetClipboardSequenceNumber();
        while (true)
        {
            var now = GetClipboardSequenceNumber();
            if (now == seen)
            {
                Thread.Sleep(1);
                continue;
            }

            seen = now;
            if (OpenClipboard(IntPtr.Zero))
            {
                Thread.Sleep(15);
                CloseClipboard();
            }
        }
    }
}
