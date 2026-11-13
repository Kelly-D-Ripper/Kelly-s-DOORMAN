using System;
using System.IO;
using System.Security.Cryptography;

namespace KellysJOINCHECK
{
    // Existing installed assets can exceed the download budget. Verification
    // streams their bytes; metadata/native inspection has separate limits.
    internal static class InstalledFilePolicy
    {
        internal const long MaximumBytes=8L*1024*1024*1024;
        internal const long MaximumMetadataDllBytes=256L*1024*1024;
        internal const long MaximumBundleInspectionBytes=2L*1024*1024*1024;

        internal static void ValidateLength(long length)
        {
            if(length<0||length>MaximumBytes)
                throw new InvalidDataException("Installed mod files above 8 GiB need manual setup. This file was not changed.");
        }

        internal static string Hash(Stream input)
        {
            if(input==null||!input.CanRead||!input.CanSeek)throw new InvalidDataException("Installed mod verification needs a readable, seekable source.");
            long length=input.Length;
            ValidateLength(length);
            input.Position=0;
            string digest;
            using(var sha=SHA256.Create())digest=BitConverter.ToString(sha.ComputeHash(input)).Replace("-","").ToLowerInvariant();
            if(input.Length!=length||input.Position!=length)throw new IOException("The installed mod file changed during verification.");
            return digest;
        }
    }
}
