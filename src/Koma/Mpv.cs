using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Interop;
using Microsoft.Win32.SafeHandles;

namespace Koma;

internal sealed class VideoHost : HwndHost
{
    protected override HandleRef BuildWindowCore(HandleRef parent)
    {
        nint window = Native.CreateWindowEx(0, "STATIC", "", 0x50000004, 0, 0, 1, 1,
            parent.Handle, 0, 0, 0);
        if (window == 0) throw new Win32Exception();
        return new(this, window);
    }

    protected override void DestroyWindowCore(HandleRef window) => Native.DestroyWindow(window.Handle);
}

internal sealed class Mpv : IDisposable
{
    private readonly PipeStream pipe;
    private readonly Process process;
    private readonly StreamWriter writer;
    private SafePipeHandle? startupPeer;
    private readonly SemaphoreSlim writes = new(1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> pending = new();
    private int request;
    private bool disposed;
    public event Action<JsonElement>? Message;
    public event Action? Disconnected;

    private Mpv(PipeStream pipe, Process process, SafePipeHandle? startupPeer)
    {
        this.pipe = pipe;
        this.process = process;
        this.startupPeer = startupPeer;
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            foreach (var response in pending.Values) response.TrySetException(new IOException("The playback engine exited."));
        };
        writer = new(pipe, new UTF8Encoding(false), leaveOpen: true) { NewLine = "\n" };
    }

    public static async Task<Mpv> Start(string executable, nint window, params string[] options)
    {
        var arguments = new List<string> {
            "--no-config", "--idle=yes", "--no-terminal", "--no-osc", "--no-ytdl", "--no-resume-playback",
            "--player-operation-mode=cplayer", "--vo=gpu-next", "--hwdec=auto-safe", "--no-window-dragging",
            "--drag-and-drop=auto", "--no-input-builtin-drag-and-drop", "--access-references=no",
            $"--wid={unchecked((uint)window.ToInt64())}"
        };
        arguments.AddRange(options);
        Mpv session = await StartInherited(executable, arguments);
        _ = session.ReadAsync();
        try
        {
            await session.Command("get_property", "mpv-version");
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    private static async Task<Mpv> StartInherited(string executable, List<string> arguments)
    {
        string name = $"koma-{Guid.NewGuid():N}";
        var endpoint = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        SafePipeHandle? inherited = null;
        try
        {
            inherited = Native.OpenPipePeer(name);
            await endpoint.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(3));
            var inheritedArgs = arguments.Append($"--input-ipc-client=handle://{inherited.DangerousGetHandle():D}").ToList();
            if (Environment.GetEnvironmentVariable("KOMA_ENGINE_LOG") is { Length: > 0 } log)
                inheritedArgs.Add("--log-file=" + log + ".inherited." + name);
            Process engine = Native.Launch(executable, inheritedArgs, inherited);
            return new(endpoint, engine, inherited);
        }
        catch
        {
            endpoint.Dispose();
            inherited?.Dispose();
            throw;
        }
    }

    public async Task<JsonElement> Command(params object[] command)
    {
        if (disposed) throw new IOException("Playback has closed.");
        int id = Interlocked.Increment(ref request);
        var response = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = response;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await writes.WaitAsync(timeout.Token).ConfigureAwait(false);
            try
            {
                await writer.WriteLineAsync(JsonSerializer.Serialize(new { command, request_id = id }).AsMemory(), timeout.Token).ConfigureAwait(false);
                await writer.FlushAsync(timeout.Token).ConfigureAwait(false);
            }
            finally { writes.Release(); }
            var answer = await response.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            Interlocked.Exchange(ref startupPeer, null)?.Dispose();
            return answer;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException("The playback engine did not respond.");
        }
        finally { pending.TryRemove(id, out _); }
    }

    private async Task ReadAsync()
    {
        try
        {
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                using var parsed = JsonDocument.Parse(line);
                JsonElement root = parsed.RootElement;
                if (root.TryGetProperty("request_id", out var id) && pending.TryGetValue(id.GetInt32(), out var request))
                {
                    string? error = root.String("error");
                    if (error == "success") request.TrySetResult(root.Value("data"));
                    else request.TrySetException(new IOException(error ?? "Playback command failed."));
                }
                else if (root.TryGetProperty("event", out _)) Message?.Invoke(root.Clone());
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or JsonException) { }
        finally
        {
            foreach (var response in pending.Values) response.TrySetException(new IOException("The playback engine disconnected."));
            if (!disposed) Disconnected?.Invoke();
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Interlocked.Exchange(ref startupPeer, null)?.Dispose();
        pipe.Dispose();
        try
        {
            if (!process.WaitForExit(300)) process.Kill();
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception) { }
        process.Dispose();
    }
}

internal static class JsonFields
{
    public static JsonElement Value(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value.Clone() : default;
    public static string? String(this JsonElement element, string name) =>
        element.Value(name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
    public static double Number(this JsonElement element) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var value) ? value : 0;
}

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public nint Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XChars, YChars, Fill, Flags;
        public short Show, ReservedSize;
        public nint ReservedData, Input, Output, Error, AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo
    {
        public nint Process, Thread;
        public int ProcessId, ThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(nint list, int count, int flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, nint value, nuint size, nint previous, nint returned);
    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(nint list);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(SafePipeHandle handle, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafePipeHandle CreateFileW([MarshalAs(UnmanagedType.LPWStr)] string path, uint access,
        uint share, nint security, uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(string application, StringBuilder command, nint processAttributes, nint threadAttributes,
        bool inherit, uint flags, nint environment, string currentDirectory, ref StartupInfo startup, out ProcessInfo info);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern nint CreateWindowEx(int exStyle, string type, string name, int style, int x, int y,
        int w, int h, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")]
    internal static extern bool DestroyWindow(nint handle);
    [DllImport("user32.dll")]
    internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]
    internal static extern bool GetCursorPos(out CursorPoint point);

    [StructLayout(LayoutKind.Sequential)]
    internal record struct CursorPoint(int X, int Y);

    public static SafePipeHandle OpenPipePeer(string name)
    {
        // Keep the inherited end unbound from .NET's IO completion port.
        var handle = CreateFileW(@"\\.\pipe\" + name, 0xc0000000, 0, 0, 3, 0x40110000, 0);
        if (!handle.IsInvalid) return handle;
        var error = new Win32Exception();
        handle.Dispose();
        throw error;
    }

    public static Process Launch(string executable, IEnumerable<string> arguments, SafePipeHandle handle)
    {
        using var console = CreateFileW("NUL", 0xc0000000, 3, 0, 3, 0, 0);
        if (console.IsInvalid) throw new Win32Exception();
        nuint bytes = 0;
        InitializeProcThreadAttributeList(0, 1, 0, ref bytes);
        nint attributes = Marshal.AllocHGlobal(checked((nint)bytes));
        nint value = Marshal.AllocHGlobal(IntPtr.Size * 2);
        bool initialized = false;
        try
        {
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref bytes)) throw new Win32Exception();
            initialized = true;
            if (!SetHandleInformation(handle, 1, 1) || !SetHandleInformation(console, 1, 1)) throw new Win32Exception();
            nint standard = console.DangerousGetHandle();
            Marshal.WriteIntPtr(value, handle.DangerousGetHandle());
            Marshal.WriteIntPtr(value, IntPtr.Size, standard);
            if (!UpdateProcThreadAttribute(attributes, 0, 0x20002, value, (nuint)IntPtr.Size * 2, 0, 0))
                throw new Win32Exception();
            var startup = new StartupInfo {
                Size = Marshal.SizeOf<StartupInfo>(), AttributeList = attributes,
                Flags = 0x100, Input = standard, Output = standard, Error = standard
            };
            var command = new StringBuilder(string.Join(' ', arguments.Prepend(executable).Select(Quote)));
            if (!CreateProcess(executable, command, 0, 0, true, 0x08080000, 0, Path.GetDirectoryName(executable)!,
                    ref startup, out var result))
                throw new Win32Exception();
            try { return Process.GetProcessById(result.ProcessId); }
            finally
            {
                CloseHandle(result.Thread);
                CloseHandle(result.Process);
            }
        }
        finally
        {
            SetHandleInformation(handle, 1, 0);
            if (initialized) DeleteProcThreadAttributeList(attributes);
            Marshal.FreeHGlobal(value);
            Marshal.FreeHGlobal(attributes);
        }
    }

    private static string Quote(string argument)
    {
        var output = new StringBuilder("\"");
        int backslashes = 0;
        foreach (char character in argument)
        {
            if (character == '\\') { backslashes++; continue; }
            output.Append('\\', character == '"' ? backslashes * 2 + 1 : backslashes).Append(character);
            backslashes = 0;
        }
        return output.Append('\\', backslashes * 2).Append('"').ToString();
    }
}
