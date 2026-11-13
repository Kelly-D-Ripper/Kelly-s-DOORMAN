using System;
using UnityEngine;
using UnityEngine.UI;

namespace KellysJOINCHECK
{
    internal static class NativeMenuStyle
    {
        internal static void Button(Button button)
        {
            var colors=ColorBlock.defaultColorBlock;
            colors.normalColor=Color.white;colors.highlightedColor=new Color(1.22f,1.22f,1.22f,1);
            colors.pressedColor=new Color(.72f,.82f,.76f,1);colors.selectedColor=colors.highlightedColor;
            colors.disabledColor=new Color(.62f,.62f,.62f,.7f);colors.fadeDuration=.08f;button.colors=colors;
        }
        internal static void Cell(RectTransform rect,int index,int columns,float top,float height,float inset)
        {
            rect.anchorMin=new Vector2(index/(float)columns,1);rect.anchorMax=new Vector2((index+1)/(float)columns,1);rect.pivot=new Vector2(.5f,1);
            rect.offsetMin=new Vector2(index==0?inset:4,-top-height);rect.offsetMax=new Vector2(index==columns-1?-inset:-4,-top);
        }
        internal static TMPro.TMP_InputField Search(RectTransform parent,TMPro.TMP_Text font,Action changed,string placeholder)
        {
            var rect=(RectTransform)new GameObject("Search mods",typeof(RectTransform),typeof(Image)).transform;rect.SetParent(parent,false);
            var fill=rect.GetComponent<Image>();fill.color=new Color(.10f,.13f,.15f,1);
            var area=(RectTransform)new GameObject("Text area",typeof(RectTransform),typeof(RectMask2D)).transform;area.SetParent(rect,false);area.anchorMin=Vector2.zero;area.anchorMax=Vector2.one;area.offsetMin=new Vector2(12,0);area.offsetMax=new Vector2(-40,0);
            TMPro.TMP_Text Text(string name,string value,Color color)
            {
                var t=new GameObject(name,typeof(RectTransform)).AddComponent<TMPro.TextMeshProUGUI>();t.transform.SetParent(area,false);t.rectTransform.anchorMin=Vector2.zero;t.rectTransform.anchorMax=Vector2.one;t.rectTransform.offsetMin=t.rectTransform.offsetMax=Vector2.zero;
                t.font=font.font;t.fontSharedMaterial=font.fontSharedMaterial;t.fontSize=18;t.text=value;t.color=color;t.richText=false;t.raycastTarget=false;t.enableWordWrapping=false;t.alignment=TMPro.TextAlignmentOptions.MidlineLeft;return t;
            }
            var input=rect.gameObject.AddComponent<TMPro.TMP_InputField>();input.targetGraphic=fill;input.textViewport=area;input.textComponent=Text("Text","",Color.white);input.placeholder=Text("Placeholder",placeholder,new Color(.58f,.63f,.67f,1));input.characterLimit=120;input.lineType=TMPro.TMP_InputField.LineType.SingleLine;input.onValueChanged.AddListener(_=>changed());input.customCaretColor=true;input.caretColor=Color.white;
            var clear=(RectTransform)new GameObject("Clear search",typeof(RectTransform),typeof(Image),typeof(Button)).transform;clear.SetParent(rect,false);clear.anchorMin=new Vector2(1,0);clear.anchorMax=Vector2.one;clear.offsetMin=new Vector2(-34,4);clear.offsetMax=new Vector2(-4,-4);clear.GetComponent<Image>().color=new Color(.20f,.24f,.26f,1);
            var b=clear.GetComponent<Button>();b.targetGraphic=clear.GetComponent<Image>();Button(b);b.onClick.AddListener(()=>{input.text="";input.ActivateInputField();});var label=Text("Clear","X",Color.white);label.transform.SetParent(clear,false);label.alignment=TMPro.TextAlignmentOptions.Center;
            return input;
        }
    }
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
