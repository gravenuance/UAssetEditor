/*
    This code has been slightly adapted from code written by @trumank as part of the repak crate: https://github.com/trumank/repak
    and from its repak_cs bindings on the gravenuance/repak fork (branch feat/marvel-rivals-variant).

    MIT License

    Copyright 2024 Truman Kilen, spuds

    Permission is hereby granted, free of charge, to any person obtaining a copy
    of this software and associated documentation files (the "Software"), to deal
    in the Software without restriction, including without limitation the rights
    to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
    copies of the Software, and to permit persons to whom the Software is
    furnished to do so, subject to the following conditions:
    The above copyright notice and this permission notice shall be included in all
    copies or substantial portions of the Software.
    THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
    IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
    FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
    AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
    LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
    OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
    SOFTWARE.
*/

#nullable enable

namespace UAssetAPI;

using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using UAssetAPI.PropertyTypes.Objects;

/// <summary>
/// Pak version.
/// </summary>
public enum PakVersion : byte
{
    /// <summary>V0</summary>
    V0 = 0,
    /// <summary>V1</summary>
    V1 = 1,
    /// <summary>V2</summary>
    V2 = 2,
    /// <summary>V3</summary>
    V3 = 3,
    /// <summary>V4</summary>
    V4 = 4,
    /// <summary>V5</summary>
    V5 = 5,
    /// <summary>V6</summary>
    V6 = 6,
    /// <summary>V7</summary>
    V7 = 7,
    /// <summary>V8A</summary>
    V8A = 8,
    /// <summary>V8B</summary>
    V8B = 9,
    /// <summary>V9</summary>
    V9 = 10,
    /// <summary>V10</summary>
    V10 = 11,
    /// <summary>V11</summary>
    V11 = 12
}

/// <summary>
/// Pak compression algorithm.
/// </summary>
public enum PakCompression : byte
{
    /// <summary>Zlib</summary>
    Zlib,
    /// <summary>Gzip</summary>
    Gzip,
    /// <summary>Oodle</summary>
    Oodle,
    /// <summary>Zstd</summary>
    Zstd,
    /// <summary>LZ4</summary>
    LZ4
}

/// <summary>A game's deviations from the standard pak format; the values match repak_bind's.</summary>
public enum PakVariant : byte
{
    /// <summary>The format Unreal Engine writes.</summary>
    Standard = 0,
    /// <summary>Marvel Rivals' encryption and padding of compressed entries.</summary>
    MarvelRivals = 1
}

internal static class NativeError
{
    /// <summary>
    /// Refuses a null string or one with an interior NUL: UTF-8 marshalling ends the native string at the NUL,
    /// so the native side would silently see only the part before it.
    /// </summary>
    public static void ThrowIfNotNativeString(string value, string paramName)
    {
        ArgumentNullException.ThrowIfNull(value, paramName);
        if (value.Contains('\0')) throw new ArgumentException("must not contain a NUL character", paramName);
    }

    /// <summary>Reads and frees a native message; a null pointer gives a generic text.</summary>
    public static string Consume(IntPtr message)
    {
        if (message == IntPtr.Zero) return "unknown error";
        try
        {
            return Marshal.PtrToStringUTF8(message) ?? "unknown error";
        }
        finally
        {
            RePakInterop.pak_cstring_drop(message);
        }
    }

    /// <summary>The failure for a native call that just returned an error, using the calling thread's last native message; when the stream being read or written threw, that exception is the inner exception.</summary>
    public static IOException Failure(string action, Exception? streamError = null) =>
        Failure(action, RePakInterop.pak_last_error(), streamError);

    public static IOException Failure(string action, IntPtr message, Exception? streamError = null) =>
        new($"{action}: {Consume(message)}", streamError);
}

public class PakBuilder : SafeHandleZeroOrMinusOneIsInvalid
{
    /// <summary>
    /// The native library ships gzipped inside this assembly and is unpacked next to it on
    /// first use. Extraction used to happen only when loading threw <see cref="DllNotFoundException"/>,
    /// so once any copy existed on disk it was reused forever - a rebuilt native library was
    /// silently ignored, and the stale copy kept answering calls. That cost real debugging
    /// time (a fix appeared not to work because the old binary was still loaded), so the
    /// unpacked file is now refreshed whenever it differs in size from the embedded copy.
    /// </summary>
    private static void EnsureNativeLibraryCurrent()
    {
        var isLinux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
        var outPath = Path.Combine(AppContext.BaseDirectory, isLinux ? "repak_bind.so" : "repak_bind.dll");
        var resourceName = isLinux ? "UAssetAPI.repak_bind.so.gz" : "UAssetAPI.repak_bind.dll.gz";

        using var resource = typeof(PropertyData).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new DllNotFoundException("Failed to find resource: " + resourceName);
        using var gzipStream = new GZipStream(resource, CompressionMode.Decompress);
        using var embedded = new MemoryStream();
        gzipStream.CopyTo(embedded);

        // Size is enough to catch a rebuild and costs one stat; a hash would reread the whole
        // file on every construction for no practical gain.
        if (File.Exists(outPath) && new FileInfo(outPath).Length == embedded.Length) return;

        try
        {
            File.WriteAllBytes(outPath, embedded.ToArray());
        }
        catch (IOException)
        {
            // Already loaded by this or another process - the on-disk copy can't be replaced
            // while mapped, and whatever is loaded is what we have to use.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public PakBuilder() : base(true)
    {
        try
        {
            EnsureNativeLibraryCurrent();
            SetHandle(RePakInterop.pak_builder_new());
        }
        catch (Exception ex)
        {
            if (ex is DllNotFoundException || ex is BadImageFormatException)
            {
                // extract dll if needed
                string outPath = Path.Combine(AppContext.BaseDirectory, RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "repak_bind.so" : "repak_bind.dll");
                string resourceName = RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "UAssetAPI.repak_bind.so.gz" : "UAssetAPI.repak_bind.dll.gz";
                using (var resource = typeof(PropertyData).Assembly.GetManifestResourceStream(resourceName))
                {
                    if (resource != null)
                    {
                        using (var file = new FileStream(outPath, FileMode.Create, FileAccess.Write))
                        {
                            using (var gzipStream = new GZipStream(resource, CompressionMode.Decompress))
                            {
                                gzipStream.CopyTo(file);
                            }
                        }
                    }
                    else
                    {
                        throw new DllNotFoundException("Failed to find resource: " + resourceName);
                    }
                }

                NativeLibrary.Load(outPath);

                SetHandle(RePakInterop.pak_builder_new());
            }
            else
            {
                throw;
            }
        }

        if (handle == IntPtr.Zero) throw NativeError.Failure("Failed to create the PakBuilder");
    }

    protected override bool ReleaseHandle()
    {
        RePakInterop.pak_builder_drop(handle);
        return true;
    }

    private void EnsureValid()
    {
        if (IsInvalid || IsClosed) throw new InvalidOperationException("PakBuilder handle invalid");
    }

    /// <summary>Every builder call consumes the native builder and returns the one to continue with, or null when it failed.</summary>
    private void Continue(IntPtr next, string action)
    {
        SetHandle(next);
        if (next == IntPtr.Zero) throw NativeError.Failure(action);
    }

    private void Consume()
    {
        SetHandleAsInvalid();
        SetHandle(IntPtr.Zero);
    }

    public PakBuilder Key(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != 32) throw new ArgumentException("Invalid AES key length", nameof(key));
        EnsureValid();
        Continue(RePakInterop.pak_builder_key(handle, key), "Failed to set the key");
        return this;
    }

    /// <summary>Selects a game's pak format; builders default to <see cref="PakVariant.Standard"/>.</summary>
    public PakBuilder Variant(PakVariant variant)
    {
        EnsureValid();
        Continue(RePakInterop.pak_builder_variant(handle, (byte)variant), $"Failed to select the variant {variant}");
        return this;
    }

    public PakBuilder Compression(PakCompression[] compressions)
    {
        ArgumentNullException.ThrowIfNull(compressions);
        EnsureValid();
        var bytes = compressions.Select(c => (byte)c).ToArray();
        Continue(RePakInterop.pak_builder_compression(handle, bytes, (nuint)bytes.Length), "Failed to set the compression");
        return this;
    }

    public PakWriter Writer(Stream stream, PakVersion version = PakVersion.V11, string mountPoint = "../../../", ulong pathHashSeed = 0)
    {
        ArgumentNullException.ThrowIfNull(stream);
        NativeError.ThrowIfNotNativeString(mountPoint, nameof(mountPoint));
        EnsureValid();

        var streamCtx = StreamCallbacks.Create(stream);

        IntPtr writerHandle = RePakInterop.pak_builder_writer(handle, streamCtx, version, mountPoint, pathHashSeed);

        // pak_builder_writer consumes the builder
        Consume();

        if (writerHandle == IntPtr.Zero)
        {
            var failure = NativeError.Failure("Failed to create the PakWriter", StreamCallbacks.ErrorOf(streamCtx.Context));
            StreamCallbacks.Free(streamCtx.Context);
            throw failure;
        }
        return new PakWriter(writerHandle, streamCtx.Context);
    }

    public PakReader Reader(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        EnsureValid();

        var streamCtx = StreamCallbacks.Create(stream);

        IntPtr readerHandle = RePakInterop.pak_builder_reader_ex(handle, streamCtx, out IntPtr errorPtr);
        var streamError = StreamCallbacks.ErrorOf(streamCtx.Context);
        StreamCallbacks.Free(streamCtx.Context);

        // pak_builder_reader_ex consumes the builder
        Consume();

        if (readerHandle == IntPtr.Zero) throw NativeError.Failure("Failed to create the PakReader", errorPtr, streamError);
        return new PakReader(readerHandle, stream);
    }
}

public class PakWriter : SafeHandleZeroOrMinusOneIsInvalid
{
    private IntPtr _streamCtx;

    public PakWriter(IntPtr handle, IntPtr streamCtx) : base(true)
    {
        SetHandle(handle);
        _streamCtx = streamCtx;
    }

    protected override bool ReleaseHandle()
    {
        RePakInterop.pak_writer_drop(handle);
        StreamCallbacks.Free(_streamCtx);
        return true;
    }

    private void EnsureValid()
    {
        if (IsInvalid || IsClosed) throw new InvalidOperationException("PakWriter handle invalid");
    }

    public void WriteFile(string path, byte[] data)
    {
        NativeError.ThrowIfNotNativeString(path, nameof(path));
        ArgumentNullException.ThrowIfNull(data);
        EnsureValid();
        int result = RePakInterop.pak_writer_write_file(handle, path, data, (nuint)data.Length);
        var streamError = StreamCallbacks.ErrorOf(_streamCtx);
        if (result != 0) throw NativeError.Failure($"Failed to write {path}", streamError);
    }

    /// <summary>Writes the index and finishes the pak; the writer cannot be used afterwards.</summary>
    public void WriteIndex()
    {
        EnsureValid();
        int result = RePakInterop.pak_writer_write_index(handle);

        // write_index consumes the writer, whatever the result
        var streamError = StreamCallbacks.ErrorOf(_streamCtx);
        var failure = result != 0 ? NativeError.Failure("Failed to write the index", streamError) : null;
        SetHandleAsInvalid();
        SetHandle(IntPtr.Zero);
        StreamCallbacks.Free(_streamCtx);

        if (failure != null) throw failure;
    }
}

public class PakReader : SafeHandleZeroOrMinusOneIsInvalid
{
    public PakReader(IntPtr handle, Stream stream) : base(true)
    {
        SetHandle(handle);
    }

    protected override bool ReleaseHandle()
    {
        RePakInterop.pak_reader_drop(handle);
        return true;
    }

    private void EnsureValid()
    {
        if (IsInvalid || IsClosed) throw new InvalidOperationException("PakReader handle invalid");
    }

    public string GetMountPoint()
    {
        EnsureValid();

        var cstring = RePakInterop.pak_reader_mount_point(handle);
        if (cstring == IntPtr.Zero) throw NativeError.Failure("Failed to read the mount point");
        return NativeError.Consume(cstring);
    }

    public PakVersion GetVersion()
    {
        EnsureValid();

        var version = RePakInterop.pak_reader_version(handle);
        if (!Enum.IsDefined((PakVersion)version)) throw NativeError.Failure("Failed to read the version");
        return (PakVersion)version;
    }

    /// <summary>The entry's bytes; a failure (a missing entry, a decompression failure, anything) throws <see cref="IOException"/> with repak's own message.</summary>
    public byte[] Get(Stream stream, string path)
    {
        ArgumentNullException.ThrowIfNull(stream);
        NativeError.ThrowIfNotNativeString(path, nameof(path));
        EnsureValid();

        var streamCtx = StreamCallbacks.Create(stream);

        int result = RePakInterop.pak_reader_get(handle, path, streamCtx, out IntPtr bufferPtr, out nuint length, out IntPtr errorPtr);

        var streamError = StreamCallbacks.ErrorOf(streamCtx.Context);
        StreamCallbacks.Free(streamCtx.Context);

        if (result != 0) throw NativeError.Failure($"Failed to get {path}", errorPtr, streamError);

        try
        {
            if (length > int.MaxValue) throw new IOException($"{path} is too large for a byte array");
            byte[] buffer = new byte[(int)length];
            Marshal.Copy(bufferPtr, buffer, 0, buffer.Length);
            return buffer;
        }
        finally
        {
            RePakInterop.pak_buffer_drop(bufferPtr, length);
        }
    }

    public string[] Files()
    {
        EnsureValid();

        IntPtr filesPtr = RePakInterop.pak_reader_files(handle, out nuint length);
        if (filesPtr == IntPtr.Zero) throw NativeError.Failure("Failed to list the pak");
        var files = new List<string>();
        try
        {
            for (nuint i = 0; i < length; i++)
            {
                IntPtr currentPtr = Marshal.ReadIntPtr(filesPtr, checked((int)i * IntPtr.Size));
                files.Add(Marshal.PtrToStringUTF8(currentPtr) ?? throw new IOException("repak returned a null file name"));
            }
        }
        finally
        {
            RePakInterop.pak_drop_files(filesPtr, length);
        }
        return files.ToArray();
    }
}

/// <summary>What the native side's callbacks need: the stream, and the exception it threw, if any.</summary>
internal sealed class StreamState(Stream stream)
{
    public Stream Stream { get; } = stream;

    public Exception? Error { get; set; }
}

internal static unsafe class StreamCallbacks
{
    // Native code keeps these function pointers for as long as a writer lives, so the delegates
    // are rooted for the life of the process rather than by whichever struct last held them.
    private static readonly RePakInterop.ReadCallback Read = ReadCallback;
    private static readonly RePakInterop.WriteCallback Write = WriteCallback;
    private static readonly RePakInterop.SeekCallback Seek = SeekCallback;
    private static readonly RePakInterop.FlushCallback Flush = FlushCallback;

    public static RePakInterop.StreamCallbacks Create(Stream stream)
    {
        return new RePakInterop.StreamCallbacks
        {
            Context = GCHandle.ToIntPtr(GCHandle.Alloc(new StreamState(stream))),
            ReadCb = Read,
            WriteCb = Write,
            SeekCb = Seek,
            FlushCb = Flush
        };
    }

    public static void Free(IntPtr streamCtx)
    {
        GCHandle.FromIntPtr(streamCtx).Free();
    }

    /// <summary>
    /// Takes the exception the stream threw during the last native call, which that call reports only as a failed result.
    /// Taking it clears it, so a writer used again after a failure never blames a later error on an old stream exception.
    /// </summary>
    public static Exception? ErrorOf(IntPtr streamCtx)
    {
        if (GCHandle.FromIntPtr(streamCtx).Target is not StreamState state) return null;
        var error = state.Error;
        state.Error = null;
        return error;
    }

    // An exception must not cross into native code: each callback keeps the stream's exception
    // for the managed caller and reports the failure through its negative return value.
    private static Stream StreamOf(IntPtr context) =>
        (GCHandle.FromIntPtr(context).Target as StreamState)?.Stream
            ?? throw new InvalidOperationException("Stream callback invoked after its stream was released");

    private static void Remember(IntPtr context, Exception error)
    {
        try
        {
            if (GCHandle.FromIntPtr(context).Target is StreamState state) state.Error ??= error;
        }
        catch (InvalidOperationException)
        {
            // The context was already released; there is nobody to report to.
        }
    }

    private static nint ReadCallback(IntPtr context, IntPtr buffer, nuint bufferLen)
    {
        try
        {
            var count = (int)Math.Min(bufferLen, (nuint)int.MaxValue);
            return StreamOf(context).Read(new Span<byte>((void*)buffer, count));
        }
        catch (Exception e)
        {
            Remember(context, e);
            return -1;
        }
    }

    private static nint WriteCallback(IntPtr context, IntPtr buffer, nuint bufferLen)
    {
        try
        {
            var stream = StreamOf(context);
            for (nuint written = 0; written < bufferLen;)
            {
                var count = (int)Math.Min(bufferLen - written, (nuint)int.MaxValue);
                stream.Write(new ReadOnlySpan<byte>((byte*)buffer + written, count));
                written += (nuint)count;
            }
            return (nint)bufferLen;
        }
        catch (Exception e)
        {
            Remember(context, e);
            return -1;
        }
    }

    private static long SeekCallback(IntPtr context, long offset, int origin)
    {
        try
        {
            return StreamOf(context).Seek(offset, (SeekOrigin)origin);
        }
        catch (Exception e)
        {
            Remember(context, e);
            return -1;
        }
    }

    private static int FlushCallback(IntPtr context)
    {
        try
        {
            StreamOf(context).Flush();
            return 0;
        }
        catch (Exception e)
        {
            Remember(context, e);
            return -1;
        }
    }
}
