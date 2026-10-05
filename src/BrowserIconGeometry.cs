using System;
using System.Collections.Generic;

namespace KellysJOINCHECK
{
    internal readonly struct IconPoint
    {
        internal readonly float X, Y;
        internal IconPoint(float x,float y) { X=x; Y=y; }
    }
    // Normalised geometry keeps the icons independent of fonts and canvas scaling.
    internal sealed class BrowserIconGeometry
    {
        internal readonly IconPoint[] Points;
        internal readonly int[] Triangles;
        private BrowserIconGeometry(List<IconPoint> points,List<int> triangles)
        { Points=points.ToArray(); Triangles=triangles.ToArray(); }
        internal static readonly BrowserIconGeometry Check = BuildCheck();
        internal static readonly BrowserIconGeometry StarOutline = BuildStar(false);
        internal static readonly BrowserIconGeometry StarFilled = BuildStar(true);
        private static BrowserIconGeometry BuildCheck()
        {
            var points = new List<IconPoint>(); var triangles = new List<int>();
            Stroke(points,triangles,new IconPoint(.16f,.49f),new IconPoint(.40f,.25f),.12f);
            Stroke(points,triangles,new IconPoint(.40f,.25f),new IconPoint(.85f,.77f),.12f);
            return new BrowserIconGeometry(points,triangles);
        }
        private static BrowserIconGeometry BuildStar(bool filled)
        {
            var edge = new IconPoint[10];
            for (int i=0;i<10;i++)
            {
                double angle=Math.PI/2-i*Math.PI/5; float radius=i%2==0 ? .44f : .20f;
                edge[i]=new IconPoint(.5f+radius*(float)Math.Cos(angle),.5f+radius*(float)Math.Sin(angle));
            }
            var points = new List<IconPoint>(); var triangles = new List<int>();
            if (filled)
            {
                points.Add(new IconPoint(.5f,.5f)); points.AddRange(edge);
                for(int i=0;i<10;i++) { triangles.Add(0); triangles.Add(i+1); triangles.Add((i+1)%10+1); }
            }
            else for(int i=0;i<10;i++) Stroke(points,triangles,edge[i],edge[(i+1)%10],.065f);
            return new BrowserIconGeometry(points,triangles);
        }
        private static void Stroke(List<IconPoint> points,List<int> triangles,IconPoint a,IconPoint b,float width)
        {
            float dx=b.X-a.X,dy=b.Y-a.Y; float length=(float)Math.Sqrt(dx*dx+dy*dy);
            float x=-dy/length*width/2,y=dx/length*width/2; int start=points.Count;
            points.Add(new IconPoint(a.X+x,a.Y+y)); points.Add(new IconPoint(b.X+x,b.Y+y));
            points.Add(new IconPoint(b.X-x,b.Y-y)); points.Add(new IconPoint(a.X-x,a.Y-y));
            triangles.Add(start); triangles.Add(start+1); triangles.Add(start+2);
            triangles.Add(start); triangles.Add(start+2); triangles.Add(start+3);
        }
    }
}
