using UAssetAPI;
using UAssetEditor.Core.AssetSources.PakWorker;

namespace UAssetEditor.Core.AssetSources;

/// <summary>Mount point and pak-format version read from an existing pak's header - see <see cref="PakMountPointReader.Read"/>.</summary>
public sealed record PakHeaderInfo(string MountPoint, PakVersion Version);

/// <summary>
/// Reads just a .pak's header (mount point, format version), without the rest of
/// <see cref="PakAssetSource"/>'s setup (which, for a pak under the large-pak threshold,
/// extracts every .uasset entry up front - wasteful when only a few dozen bytes of header
/// are needed), e.g. to pre-fill the Pack Folder dialog from an existing game pak the user
/// hasn't otherwise opened this session. Goes through the same out-of-process worker as
/// <see cref="PakAssetSource"/> - opening a pak's index is exactly the operation that
/// crashes on the confirmed real-world repro, so this needs the same crash isolation.
/// </summary>
public static class PakMountPointReader
{
    public static PakHeaderInfo Read(string pakPath, byte[]? aesKey = null)
    {
        using var reader = new PakReaderHandle(PakWorkerProcess.Shared, pakPath, aesKey);
        reader.OpenAsync().GetAwaiter().GetResult();
        return new PakHeaderInfo(reader.MountPoint, reader.Version);
    }
}
