using System;
using System.IO;
using System.Text;

namespace KellysJOINCHECK
{
    internal static class SupportLogTail
    {
        private const int MaximumBytes=8192;
        internal static string Read(string file)
        {
            try
            {
                if((File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0)return "Restart helper log unavailable: linked files are not included.";
                using(var stream=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
                {
                    long start=Math.Max(0,stream.Length-MaximumBytes);stream.Seek(start,SeekOrigin.Begin);
                    var bytes=new byte[MaximumBytes];int used=0,count;
                    while(used<bytes.Length&&(count=stream.Read(bytes,used,bytes.Length-used))>0)used+=count;
                    string tail=Encoding.UTF8.GetString(bytes,0,used);
                    if(start>0)
                    {
                        int newline=tail.IndexOf('\n');
                        tail="[earlier helper log omitted]\n"+(newline<0?"[oversized helper line omitted]":tail.Substring(newline+1));
                    }
                    return "Restart helper log (historical, last written "+File.GetLastWriteTimeUtc(file).ToString("o")+"):\n"+tail;
                }
            }
            catch(FileNotFoundException){return "Restart helper log: not created yet.";}
            catch(DirectoryNotFoundException){return "Restart helper log: not created yet.";}
            catch(Exception ex){return "Restart helper log unavailable: "+ex.GetType().Name+": "+ex.Message;}
        }
    }
}
