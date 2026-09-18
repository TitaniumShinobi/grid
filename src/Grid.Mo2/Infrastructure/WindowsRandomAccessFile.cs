using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Grid.Mo2.Models;
using Grid.Mo2.Services;
using Microsoft.Win32.SafeHandles;

namespace Grid.Mo2.Infrastructure;

public sealed class WindowsRandomAccessFileFactory : IMo2RandomAccessFileFactory
{
    public Task<IMo2RandomAccessFile> OpenReadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        SafeFileHandle? handle = null;
        try
        {
            handle = File.OpenHandle(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                FileOptions.Asynchronous | FileOptions.RandomAccess);
            IMo2RandomAccessFile result = new WindowsRandomAccessFile(handle);
            handle = null;
            return Task.FromResult(result);
        }
        finally
        {
            handle?.Dispose();
        }
    }
}

internal sealed class WindowsRandomAccessFile : IMo2RandomAccessFile
{
    private readonly SafeFileHandle handle;
    private bool disposed;

    public WindowsRandomAccessFile(SafeFileHandle handle)
    {
        this.handle = handle ?? throw new ArgumentNullException(nameof(handle));
        InitialStamp = ReadStamp(handle);
        Length = InitialStamp.Identity.Length;
    }

    public long Length { get; }

    public Mo2RandomAccessStamp InitialStamp { get; }

    public ValueTask<int> ReadAsync(
        long offset,
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (offset > Length || buffer.Length > Length - offset)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "The requested range is outside the observed file.");
        }

        return RandomAccess.ReadAsync(handle, buffer, offset, cancellationToken);
    }

    public ValueTask<Mo2RandomAccessStamp> GetCurrentStampAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ReadStamp(handle));
    }

    public ValueTask DisposeAsync()
    {
        if (!disposed)
        {
            disposed = true;
            handle.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private static Mo2RandomAccessStamp ReadStamp(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var information))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The file identity could not be observed.");
        }

        var path = GetFinalPath(handle);
        var fileIndex = ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow;
        var length = checked(((long)information.FileSizeHigh << 32) | information.FileSizeLow);
        var lastWrite = ((long)information.LastWriteTimeHigh << 32) | information.LastWriteTimeLow;
        return new(
            new(information.VolumeSerialNumber, fileIndex, length, lastWrite),
            path);
    }

    private static string GetFinalPath(SafeFileHandle handle)
    {
        var capacity = 512;
        while (capacity <= 32_768)
        {
            var buffer = new StringBuilder(capacity);
            var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The final path could not be resolved.");
            }

            if (length < buffer.Capacity)
            {
                var value = buffer.ToString();
                if (value.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("Remote final paths are not supported.");
                }

                return value.StartsWith(@"\\?\", StringComparison.Ordinal) ? value[4..] : value;
            }

            capacity = checked((int)length + 1);
        }

        throw new PathTooLongException("The final path exceeds the supported observation limit.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle hFile,
        out ByHandleFileInformation lpFileInformation);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle hFile,
        StringBuilder lpszFilePath,
        uint cchFilePath,
        uint dwFlags);
}
