using System;

namespace KellysJOINCHECK
{
    internal static class ModImageHeader
    {
        internal static bool Allowed(byte[] data)
        {
            if(data.Length<24) return false;
            if(data[0]==137&&data[1]==80&&data[2]==78&&data[3]==71)
                return Size(Big(data,16),Big(data,20));
            if(data[0]!=255||data[1]!=216) return false;
            int p=2;
            while(p+4<data.Length)
            {
                if(data[p++]!=255) return false;
                while(p<data.Length&&data[p]==255) p++;
                if(p>=data.Length) return false; int marker=data[p++];
                if(marker==217||marker==218) return false;
                if(marker==1 || marker>=208&&marker<=215) continue;
                int length=(data[p]<<8)|data[p+1];
                if(length<2||p+length>data.Length) return false;
                if(marker>=192&&marker<=207&&marker!=196&&marker!=200&&marker!=204)
                {
                    if(length<7) return false;
                    return Size((data[p+5]<<8)|data[p+6],(data[p+3]<<8)|data[p+4]);
                }
                p+=length;
            }
            return false;
        }
        private static uint Big(byte[] data,int p)=>(uint)data[p]<<24|(uint)data[p+1]<<16|(uint)data[p+2]<<8|data[p+3];
        private static bool Size(long width,long height)=>width>0&&height>0&&width<=8192&&height<=8192&&width*height<=16*1024*1024;
    }
}
