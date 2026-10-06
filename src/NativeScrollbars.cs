using System;
using UnityEngine;
using UnityEngine.UI;

namespace KellysJOINCHECK
{
    // All DOORMAN-owned scroll views reserve the same gutter. Permanent tracks
    // keep their width stable as content changes; Unity drives the thumb size.
    internal static class NativeScrollbars
    {
        internal const float Gutter=24,Width=18;

        internal static void Attach(ScrollRect scroll)
        {
            if(scroll==null||scroll.viewport==null||scroll.content==null)
                throw new InvalidOperationException("A scrollbar needs its scroll viewport and content.");
            if(scroll.verticalScrollbar!=null) return;
            scroll.viewport.offsetMax=new Vector2(-Gutter,scroll.viewport.offsetMax.y);
            var hit=scroll.viewport.GetComponent<Graphic>();
            if(hit!=null) hit.canvasRenderer.cullTransparentMesh=false;

            var rail=Rect("DOORMAN Scrollbar",scroll.transform);
            rail.gameObject.SetActive(false);
            rail.anchorMin=new Vector2(1,0);rail.anchorMax=Vector2.one;
            rail.offsetMin=new Vector2(-Width,0);rail.offsetMax=Vector2.zero;
            var track=rail.gameObject.AddComponent<Image>();track.color=new Color(.10f,.13f,.15f,1);
            var outline=rail.gameObject.AddComponent<Outline>();outline.effectColor=new Color(.25f,.30f,.33f,1);outline.effectDistance=new Vector2(1,-1);
            var area=Rect("Sliding area",rail);Stretch(area,2);
            var thumb=Rect("Handle",area);Stretch(thumb,0);
            var fill=thumb.gameObject.AddComponent<Image>();fill.color=Color.white;
            var bar=rail.gameObject.AddComponent<Scrollbar>();bar.targetGraphic=fill;bar.handleRect=thumb;
            bar.direction=Scrollbar.Direction.BottomToTop;
            var navigation=bar.navigation;navigation.mode=Navigation.Mode.None;bar.navigation=navigation;
            var colours=ColorBlock.defaultColorBlock;
            colours.normalColor=new Color(.43f,.51f,.55f,1);
            colours.highlightedColor=new Color(.61f,.70f,.73f,1);
            colours.pressedColor=new Color(.29f,.78f,.47f,1);
            colours.selectedColor=colours.highlightedColor;
            colours.disabledColor=new Color(.25f,.30f,.33f,1);
            colours.colorMultiplier=1;colours.fadeDuration=.08f;bar.colors=colours;
            scroll.vertical=true;scroll.verticalScrollbar=bar;
            scroll.verticalScrollbarVisibility=ScrollRect.ScrollbarVisibility.Permanent;
            rail.gameObject.SetActive(true);
        }

        private static RectTransform Rect(string name,Transform parent)
        {var rect=(RectTransform)new GameObject(name,typeof(RectTransform)).transform;rect.SetParent(parent,false);return rect;}
        private static void Stretch(RectTransform rect,float inset)
        {rect.anchorMin=Vector2.zero;rect.anchorMax=Vector2.one;rect.offsetMin=new Vector2(inset,inset);rect.offsetMax=new Vector2(-inset,-inset);}
    }
}
