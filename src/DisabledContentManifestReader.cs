using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace KellysJOINCHECK
{
    internal static class DisabledContentManifestReader
    {
        // Native metadata inspection has a separate limit from streamed file
        // hashing and whole-unit moves; bundles are never copied to byte arrays.
        internal const long MaximumBundleBytes=InstalledFilePolicy.MaximumBundleInspectionBytes;
        private const uint MaximumHeaderMetadataBytes=16*1024*1024;

        // The caller runs this on Unity's main thread after Blueprinter finishes
        // loading and menu safety passes. Caller retains ownership of the stream.
        internal static DisabledBundleInfo Read(Stream stream)
        {
            if(stream==null||!stream.CanRead||!stream.CanSeek)
                throw new InvalidDataException("The disabled content source must be a readable, seekable stream.");
            long length=stream.Length;
            if(length<32||length>MaximumBundleBytes)
                throw new InvalidDataException("The disabled content bundle is empty, truncated or too large.");
            ValidateHeader(stream,length);

            AssetBundle? bundle=null;
            try
            {
                bundle=AssetBundle.LoadFromStream(stream);
                if(!bundle)
                    throw new InvalidDataException("The disabled content bundle could not be inspected. It may be damaged or duplicate content already loaded in the game.");
                var manifest=bundle.LoadAsset<TextAsset>("patch_manifest");
                if(!manifest)
                    throw new InvalidDataException("The disabled content bundle has no patch_manifest metadata.");
                return DisabledContentMetadata.Parse(bundle.name,manifest.bytes);
            }
            catch(Exception ex) when(!(ex is InvalidDataException))
            { throw new InvalidDataException("The disabled content bundle could not be inspected: "+ex.GetType().Name+".",ex); }
            finally
            {
                // Only the TextAsset is read. No prefabs, plugin assemblies,
                // registry entries, loader operations or patches are loaded.
                if(bundle!=null&&bundle)bundle.Unload(true);
            }
        }

        private static void ValidateHeader(Stream stream,long length)
        {
            stream.Position=0;
            try
            {
                byte[] magic=new byte[8];
                for(int i=0;i<magic.Length;i++)magic[i]=(byte)Byte(stream);
                if(Encoding.ASCII.GetString(magic)!="UnityFS\0")
                    throw new InvalidDataException("The disabled .nobp source is not a supported UnityFS content bundle.");
                uint format=UInt32(stream);
                if(format<6||format>8)
                    throw new InvalidDataException("The disabled content bundle uses an unsupported UnityFS format.");
                VersionText(stream);VersionText(stream);
                ulong declared=((ulong)UInt32(stream)<<32)|UInt32(stream);
                uint compressed=UInt32(stream),uncompressed=UInt32(stream),flags=UInt32(stream);
                if(declared!=(ulong)length||compressed==0||uncompressed==0||compressed>MaximumHeaderMetadataBytes||uncompressed>MaximumHeaderMetadataBytes||compressed>length-stream.Position||(flags&63)>3)
                    throw new InvalidDataException("The disabled content bundle header has invalid sizes or compression metadata.");
            }
            finally { stream.Position=0; }
        }
        private static int Byte(Stream stream)
        {
            int value=stream.ReadByte();
            if(value<0)throw new InvalidDataException("The disabled content bundle header is truncated.");
            return value;
        }
        private static uint UInt32(Stream stream)=>((uint)Byte(stream)<<24)|((uint)Byte(stream)<<16)|((uint)Byte(stream)<<8)|(uint)Byte(stream);
        private static void VersionText(Stream stream)
        {
            for(int i=0;i<128;i++)
            {
                int value=Byte(stream);
                if(value==0&&i>0)return;
                if(value<32||value>126)throw new InvalidDataException("The disabled content bundle has invalid UnityFS version metadata.");
            }
            throw new InvalidDataException("The disabled content bundle has oversized UnityFS version metadata.");
        }
    }
}
