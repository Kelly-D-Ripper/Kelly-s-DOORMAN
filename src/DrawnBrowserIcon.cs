using UnityEngine;
using UnityEngine.UI;

namespace KellysJOINCHECK
{
    internal sealed class DrawnBrowserIcon : MaskableGraphic
    {
        private BrowserIconGeometry geometry = BrowserIconGeometry.Check;
        internal void Use(BrowserIconGeometry value)
        {
            if (ReferenceEquals(geometry,value)) return;
            geometry=value; SetVerticesDirty();
        }
        protected override void OnPopulateMesh(VertexHelper mesh)
        {
            mesh.Clear(); var rect=GetPixelAdjustedRect();
            float size=Mathf.Min(rect.width,rect.height);
            float left=rect.center.x-size/2,bottom=rect.center.y-size/2;
            foreach(var point in geometry.Points)
                mesh.AddVert(new Vector3(left+point.X*size,bottom+point.Y*size,0),color,Vector2.zero);
            for(int i=0;i<geometry.Triangles.Length;i+=3)
                mesh.AddTriangle(geometry.Triangles[i],geometry.Triangles[i+1],geometry.Triangles[i+2]);
        }
    }
}
