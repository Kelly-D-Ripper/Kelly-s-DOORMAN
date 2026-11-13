using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Xml;

namespace KellysJOINCHECK
{
    internal sealed class DisabledBundleInfo
    {
        internal string Id="",Name="",Version="",GameVersion="";
        internal int Schema;
    }

    internal static class DisabledContentMetadata
    {
        internal const int MaximumJsonBytes=24*1024*1024;
        internal const string DefaultGameVersion="0.34.2";

        // Blueprinter's bundle identity comes from AssetBundle.name, not a
        // registry ID, DLL filename, resource filename or manifest display name.
        // Parse only scalar metadata; patch and operation payloads never become
        // loader objects and cannot execute while a disabled pack is inspected.
        internal static DisabledBundleInfo Parse(string bundleName,byte[] json)
        {
            if(!Text(bundleName,256)||bundleName.Any(c=>c=='/'||c=='\\'||c==':'||c=='|'||c=='#')||bundleName.Contains("_--"))
                throw new InvalidDataException("The disabled pack has an invalid content ID.");
            if(json==null||json.Length==0||json.Length>MaximumJsonBytes)
                throw new InvalidDataException("The disabled pack's patch_manifest is empty or too large.");

            try
            {
                // Reject damaged UTF-8 rather than silently replacing bytes in
                // an identity/version that must match server metadata exactly.
                CheckBoundary(new UTF8Encoding(false,true).GetString(json));
                var quotas=new XmlDictionaryReaderQuotas {
                    MaxDepth=32,MaxStringContentLength=MaximumJsonBytes,
                    MaxArrayLength=MaximumJsonBytes,MaxBytesPerRead=4096,
                    MaxNameTableCharCount=MaximumJsonBytes
                };
                int offset=json.Length>=3&&json[0]==0xEF&&json[1]==0xBB&&json[2]==0xBF?3:0;
                int end=json.Length;while(end>offset&&(json[end-1]==32||json[end-1]==9||json[end-1]==10||json[end-1]==13))end--;
                var result=new DisabledBundleInfo { Id=bundleName,Schema=3,GameVersion=DefaultGameVersion };
                var seen=new HashSet<string>(StringComparer.Ordinal);
                using(var reader=JsonReaderWriterFactory.CreateJsonReader(json,offset,end-offset,quotas))
                {
                    reader.MoveToContent();
                    if(reader.LocalName!="root"||reader.GetAttribute("type")!="object")throw new InvalidDataException("The disabled pack's patch_manifest must be a JSON object.");
                    reader.ReadStartElement();
                    while(!reader.EOF&&!(reader.NodeType==XmlNodeType.EndElement&&reader.Depth==0))
                    {
                        if(reader.NodeType!=XmlNodeType.Element||reader.Depth!=1){reader.Read();continue;}
                        string key=reader.LocalName;
                        if(key!="schemaVersion"&&key!="modName"&&key!="modVersion"&&key!="gameVersion"){reader.Skip();continue;}
                        if(!seen.Add(key))throw new InvalidDataException("The disabled pack's patch_manifest repeats a metadata field.");
                        string kind=reader.GetAttribute("type")??"";
                        if(key=="gameVersion"&&kind=="null"){reader.Skip();continue;}
                        if(key=="schemaVersion"&&kind!="number")throw new InvalidDataException("The disabled pack uses an unsupported manifest schema.");
                        if(key!="schemaVersion"&&kind!="string")throw new InvalidDataException("The disabled pack's manifest metadata must use text values.");
                        string value=reader.ReadElementContentAsString();
                        if(key=="schemaVersion")
                        {if(!int.TryParse(value,NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out result.Schema)||result.Schema<0||result.Schema>3)throw new InvalidDataException("The disabled pack uses an unsupported manifest schema.");}
                        else if(key=="modName")result.Name=value;
                        else if(key=="modVersion")result.Version=value;
                        else if(value.Length>0)result.GameVersion=value;
                    }
                }
                if(!Text(result.Name,256)||!Text(result.Version,128)||result.Version.Any(c=>c=='_'||c=='|'||c=='#')||!Text(result.GameVersion,128))
                    throw new InvalidDataException("The disabled pack has a missing or invalid name/version.");
                return result;
            }
            catch(Exception ex) when(ex is XmlException||ex is DecoderFallbackException||ex is System.Runtime.Serialization.SerializationException||ex is FormatException)
            { throw new InvalidDataException("The disabled pack's patch_manifest JSON could not be read.",ex); }
        }

        private static void CheckBoundary(string json)
        {
            // The framework JSON reader can stop after one value and ignore
            // trailing bytes. Require exactly one complete top-level object;
            // the JSON reader still validates the grammar inside that object.
            int start=0;
            if(json.Length>0&&json[0]=='\uFEFF')start++;
            while(start<json.Length&&White(json[start]))start++;
            if(start==json.Length||json[start]!='{')throw new InvalidDataException("The disabled pack's patch_manifest must be a JSON object.");
            int depth=0;bool quoted=false,escaped=false;
            for(int i=start;i<json.Length;i++)
            {
                char value=json[i];
                if(quoted)
                {
                    if(escaped)escaped=false;
                    else if(value=='\\')escaped=true;
                    else if(value=='\"')quoted=false;
                    continue;
                }
                if(value=='\"')quoted=true;
                else if(value=='{'||value=='[')
                { if(++depth>32)throw new InvalidDataException("The disabled pack's patch_manifest is nested too deeply."); }
                else if(value=='}'||value==']')
                {
                    if(--depth!=0)continue;
                    for(int tail=i+1;tail<json.Length;tail++)if(!White(json[tail]))
                        throw new InvalidDataException("The disabled pack's patch_manifest has trailing JSON data.");
                    return;
                }
            }
            throw new InvalidDataException("The disabled pack's patch_manifest JSON is incomplete.");
        }
        private static bool White(char value)=>value==' '||value=='\t'||value=='\r'||value=='\n';
        private static bool Text(string? value,int maximum)=>!string.IsNullOrWhiteSpace(value)&&value!.Length<=maximum&&value==value.Trim()&&!value.Any(char.IsControl);
    }
}


