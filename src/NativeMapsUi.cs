using System;
using System.Collections.Generic;
using System.Linq;
using NuclearOption.Networking.Lobbies;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace KellysJOINCHECK
{
    internal sealed class NativeMapsUi : IDisposable
    {
        private readonly RectTransform parent,listContent,previewContent,pictureBox,links,statusContent;
        private readonly GameObject root;
        private readonly TMP_Text font,name,byline,state,description,credits,notice,previewHint,status;
        private readonly Image spriteImage;
        private readonly RawImage textureImage;
        private readonly AspectRatioFitter textureAspect;
        private readonly RectTransform noticeBox;
        private readonly Button refresh;
        private readonly ScrollRect previewScroll;
        private readonly Action<MapEntry> selectedChanged;
        private readonly Dictionary<Button,MapEntry> rows=new Dictionary<Button,MapEntry>();
        private readonly List<GameObject> decorations=new List<GameObject>();
        private readonly List<Button> linkButtons=new List<Button>();
        private MapEntry? selected;
        private bool busy;
        private Vector2 layoutSize;
        internal bool IsOpen=>root!=null&&root.activeSelf;

        internal NativeMapsUi(RectTransform parent,TMP_Text font,Action<MapEntry> selectedChanged,UnityAction refreshMaps)
        {
            this.parent=parent;this.font=font;this.selectedChanged=selectedChanged;
            var container=Rect("Maps",parent);Stretch(container);root=container.gameObject;root.SetActive(false);
            try
            {
                var list=Rect("Installed maps",container);list.anchorMin=Vector2.zero;list.anchorMax=new Vector2(.42f,1);list.offsetMin=new Vector2(24,152);list.offsetMax=new Vector2(-10,-112);listContent=Scroll(list);
                var preview=Rect("Map preview",container);preview.anchorMin=new Vector2(.42f,0);preview.anchorMax=Vector2.one;preview.offsetMin=new Vector2(10,152);preview.offsetMax=new Vector2(-24,-112);preview.gameObject.AddComponent<Image>().color=new Color(.12f,.15f,.17f,1);previewContent=Scroll(preview);previewScroll=preview.GetComponent<ScrollRect>();
                name=Text("Map name",previewContent,26);byline=Text("Author and version",previewContent,17);byline.color=LobbyListItem.TextMutedColor;state=Text("Map state",previewContent,17);state.color=LobbyListItem.TextMutedColor;
                pictureBox=Rect("Map image",previewContent);pictureBox.gameObject.AddComponent<Image>().color=new Color(.10f,.12f,.14f,1);
                var spriteRect=Rect("Borrowed sprite",pictureBox);Stretch(spriteRect);spriteImage=spriteRect.gameObject.AddComponent<Image>();spriteImage.raycastTarget=false;spriteImage.preserveAspect=true;
                var textureRect=Rect("Borrowed texture",pictureBox);Stretch(textureRect);textureImage=textureRect.gameObject.AddComponent<RawImage>();textureImage.raycastTarget=false;textureAspect=textureRect.gameObject.AddComponent<AspectRatioFitter>();textureAspect.aspectMode=AspectRatioFitter.AspectMode.FitInParent;
                previewHint=Text("Image status",previewContent,17);previewHint.color=LobbyListItem.TextMutedColor;
                description=Text("Description",previewContent,20);credits=Text("Credits",previewContent,18);credits.color=LobbyListItem.TextMutedColor;
                noticeBox=Rect("Map notice",previewContent);noticeBox.gameObject.AddComponent<Image>().color=new Color(.16f,.20f,.22f,1);notice=Text("Notice",noticeBox,18);notice.color=new Color(.92f,.80f,.32f,1);
                links=Rect("Map links",previewContent);
                var statusArea=Rect("Map status",container);Bottom(statusArea,24,64,-24,66);statusContent=Scroll(statusArea);status=Text("Status",statusContent,17);status.color=LobbyListItem.TextMutedColor;
                refresh=Button("Refresh maps",container,()=>{if(!busy)refreshMaps();});Bottom((RectTransform)refresh.transform,24,18,-210,38);refresh.GetComponent<Image>().color=LobbyListItem.StatusSuccessColor;refresh.GetComponentInChildren<TMP_Text>(true).color=new Color(.08f,.12f,.10f,1);
                ClearImages();DrawPreview(null);
            }
            catch {UnityEngine.Object.Destroy(root);throw;}
        }

        internal void Show(){root.SetActive(true);Canvas.ForceUpdateCanvases();DrawPreview(selected);}
        internal void Hide(){ClearImages();root.SetActive(false);}
        internal void Set(IReadOnlyList<MapEntry> maps,string message)
        {
            string previous=selected?.FilePath??"";
            foreach(var row in rows.Keys){row.gameObject.SetActive(false);UnityEngine.Object.Destroy(row.gameObject);}rows.Clear();foreach(var item in decorations)UnityEngine.Object.Destroy(item);decorations.Clear();
            float y=0;
            foreach(var item in maps.Where(m=>m!=null))
            {
                var map=item;var row=Button("",listContent,()=>{if(!busy)Select(map,true);});Top((RectTransform)row.transform,0,y,0,82);rows.Add(row,map);
                var title=row.GetComponentInChildren<TMP_Text>(true);title.text=Value(map.Name,"Unnamed map");title.fontSize=20;title.fontSizeMax=20;title.fontSizeMin=15;title.alignment=TextAlignmentOptions.MidlineLeft;Top(title.rectTransform,12,7,-12,32);
                var detail=Text("State",(RectTransform)row.transform,16);detail.text=(string.IsNullOrWhiteSpace(map.Version)?"":"Version "+map.Version+" | ")+Value(map.State,"Installed map");detail.color=LobbyListItem.TextMutedColor;detail.enableWordWrapping=false;detail.overflowMode=TextOverflowModes.Ellipsis;Top(detail.rectTransform,12,44,-12,26);y+=90;
            }
            if(rows.Count==0)
            {var empty=Text("No installed maps",listContent,18);empty.text="No installed maps found.";empty.color=LobbyListItem.TextMutedColor;Top(empty.rectTransform,12,12,-12,60);decorations.Add(empty.gameObject);y=84;}
            listContent.sizeDelta=new Vector2(0,y);status.text=message;
            var next=previous.Length==0?null:rows.Values.FirstOrDefault(m=>string.Equals(m.FilePath,previous,StringComparison.OrdinalIgnoreCase));
            Select(next??maps.FirstOrDefault(m=>m!=null),true);Busy(busy);
        }
        internal void RefreshPreview(MapEntry item)
        {if(IsOpen&&selected==item)DrawPreview(item);}
        internal void Busy(bool value)
        {busy=value;refresh.interactable=!value;foreach(var row in rows.Keys)row.interactable=!value;foreach(var link in linkButtons)link.interactable=!value;}
        internal void Tick(){if(parent.rect.size!=layoutSize)Layout();}
        private void Select(MapEntry? item,bool notify)
        {
            bool changed=!string.Equals(selected?.FilePath,item?.FilePath,StringComparison.OrdinalIgnoreCase);
            selected=item;foreach(var row in rows)row.Key.GetComponent<Image>().color=row.Value==item?new Color(.20f,.34f,.27f,1):new Color(.12f,.15f,.17f,1);
            DrawPreview(item);
            if(changed)previewScroll.verticalNormalizedPosition=1;
            if(item!=null&&notify&&!busy)selectedChanged(item);
        }
        private void DrawPreview(MapEntry? item)
        {
            ClearImages();foreach(var link in linkButtons){link.gameObject.SetActive(false);UnityEngine.Object.Destroy(link.gameObject);}linkButtons.Clear();
            name.text=item==null?"No maps found":Value(item.Name,"Unnamed map");byline.text=item==null?"":Value(item.Author,"No author supplied")+(string.IsNullOrWhiteSpace(item.Version)?"":" | "+item.Version);state.text=item==null?"":Value(item.State,"Installed map");
            description.text=item==null?"Refresh after adding or subscribing to a map.":Value(item.Description,"No description available.");credits.text=item==null?"":"Credits\n"+Value(item.Credits,"No credits supplied.");notice.text=item?.Notice??"";noticeBox.gameObject.SetActive(!string.IsNullOrWhiteSpace(notice.text));
            try
            {
                // These assets belong to the map loader or its caller. This UI
                // only borrows references and never reads back or destroys them.
                if(item?.PreviewSprite!=null){spriteImage.sprite=item.PreviewSprite;spriteImage.gameObject.SetActive(true);pictureBox.gameObject.SetActive(true);}
                else if(item?.PreviewTexture!=null)
                {textureImage.texture=item.PreviewTexture;textureAspect.aspectRatio=item.PreviewTexture.height>0?(float)item.PreviewTexture.width/item.PreviewTexture.height:1;textureImage.gameObject.SetActive(true);pictureBox.gameObject.SetActive(true);}
            }
            catch {ClearImages();}
            previewHint.text=item!=null&&!pictureBox.gameObject.activeSelf?"No preview image available.":"";previewHint.gameObject.SetActive(previewHint.text.Length>0);
            if(item?.Links!=null)
                foreach(var link in item.Links.Where(l=>l!=null&&ModManifest.SafeLink(l.Url)).Take(6))
                {var chosen=link;var button=Button(Value(chosen.Label,"Website"),links,()=>{if(!busy&&ModManifest.SafeLink(chosen.Url))Application.OpenURL(chosen.Url);});button.interactable=!busy;linkButtons.Add(button);}
            Layout();
        }
        private void ClearImages()
        {spriteImage.sprite=null;textureImage.texture=null;spriteImage.gameObject.SetActive(false);textureImage.gameObject.SetActive(false);pictureBox.gameObject.SetActive(false);}
        private void Layout()
        {
            layoutSize=parent.rect.size;float width=Mathf.Max(1,previewContent.rect.width-36),top=16;
            top=Place(name,18,top,-18,width,40)+6;top=Place(byline,18,top,-18,width,24)+8;top=Place(state,18,top,-18,width,24)+14;
            if(pictureBox.gameObject.activeSelf){float height=Mathf.Min(280,width*.55f);Top(pictureBox,18,top,-18,height);top+=height+18;}
            if(previewHint.gameObject.activeSelf)top=Place(previewHint,18,top,-18,width,24)+14;
            top=Place(description,18,top,-18,width,60)+18;
            if(credits.text.Length>0)top=Place(credits,18,top,-18,width,44)+18;
            if(noticeBox.gameObject.activeSelf)
            {float height=Measure(notice,width-24,40);Top(noticeBox,18,top,-18,height+24);Top(notice.rectTransform,12,12,-12,height);top+=height+42;}
            Top(links,18,top,-18,linkButtons.Count*46);for(int i=0;i<linkButtons.Count;i++)Top((RectTransform)linkButtons[i].transform,0,i*46,0,38);previewContent.sizeDelta=new Vector2(0,top+linkButtons.Count*46+18);
            float statusWidth=Mathf.Max(1,statusContent.rect.width),statusHeight=Measure(status,statusWidth,34);Top(status.rectTransform,0,0,0,statusHeight);statusContent.sizeDelta=new Vector2(0,statusHeight);
        }
        private static string Value(string? value,string missing)=>string.IsNullOrWhiteSpace(value)?missing:value!;
        private static float Measure(TMP_Text text,float width,float minimum)=>Mathf.Max(minimum,text.GetPreferredValues(text.text,width,0).y+8);
        private static float Place(TMP_Text text,float left,float top,float right,float width,float minimum)
        {float height=Measure(text,width,minimum);Top(text.rectTransform,left,top,right,height);return top+height;}
        private TMP_Text Text(string name,RectTransform parent,float size)
        {var text=Rect(name,parent).gameObject.AddComponent<TextMeshProUGUI>();text.font=font.font;text.fontSharedMaterial=font.fontSharedMaterial;text.fontSize=size;text.color=Color.white;text.richText=false;text.raycastTarget=false;text.enableWordWrapping=true;text.overflowMode=TextOverflowModes.Overflow;return text;}
        private Button Button(string label,RectTransform parent,UnityAction action)
        {var rect=Rect(label,parent);var fill=rect.gameObject.AddComponent<Image>();fill.color=new Color(.20f,.24f,.26f,1);var button=rect.gameObject.AddComponent<Button>();button.targetGraphic=fill;button.onClick.AddListener(action);var nav=button.navigation;nav.mode=Navigation.Mode.None;button.navigation=nav;var text=Text("Label",rect,18);Stretch(text.rectTransform,6);text.text=label;text.alignment=TextAlignmentOptions.Center;text.enableWordWrapping=false;text.overflowMode=TextOverflowModes.Ellipsis;text.enableAutoSizing=true;text.fontSizeMin=14;text.fontSizeMax=18;return button;}
        private static RectTransform Scroll(RectTransform parent)
        {var viewport=Rect("Viewport",parent);Stretch(viewport);viewport.gameObject.AddComponent<RectMask2D>();var hit=viewport.gameObject.AddComponent<Image>();hit.color=Color.clear;hit.canvasRenderer.cullTransparentMesh=false;var content=Rect("Content",viewport);content.anchorMin=new Vector2(0,1);content.anchorMax=Vector2.one;content.pivot=new Vector2(.5f,1);content.offsetMin=content.offsetMax=Vector2.zero;var scroll=parent.gameObject.AddComponent<ScrollRect>();scroll.horizontal=false;scroll.viewport=viewport;scroll.content=content;scroll.movementType=ScrollRect.MovementType.Clamped;scroll.scrollSensitivity=40;NativeScrollbars.Attach(scroll);return content;}
        private static RectTransform Rect(string name,Transform parent){var rect=(RectTransform)new GameObject(name,typeof(RectTransform)).transform;rect.SetParent(parent,false);return rect;}
        private static void Stretch(RectTransform rect,float inset=0){rect.anchorMin=Vector2.zero;rect.anchorMax=Vector2.one;rect.offsetMin=new Vector2(inset,inset);rect.offsetMax=new Vector2(-inset,-inset);}
        private static void Top(RectTransform rect,float left,float top,float right,float height){rect.anchorMin=new Vector2(0,1);rect.anchorMax=Vector2.one;rect.pivot=new Vector2(.5f,1);rect.offsetMin=new Vector2(left,-top-height);rect.offsetMax=new Vector2(right,-top);}
        private static void Bottom(RectTransform rect,float left,float bottom,float right,float height){rect.anchorMin=Vector2.zero;rect.anchorMax=new Vector2(1,0);rect.offsetMin=new Vector2(left,bottom);rect.offsetMax=new Vector2(right,bottom+height);}
        public void Dispose(){Hide();UnityEngine.Object.Destroy(root);}
    }
}
