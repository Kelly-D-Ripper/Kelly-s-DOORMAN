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
        private readonly RectTransform parent,listContent,previewContent,pictureBox,links,statusContent,requisiteBox;
        private readonly GameObject root;
        private readonly TMP_Text font,name,byline,state,description,credits,notice,previewHint,status,requisiteTitle,requisiteSummary;
        private readonly Image spriteImage;
        private readonly RawImage textureImage;
        private readonly AspectRatioFitter textureAspect;
        private readonly RectTransform noticeBox;
        private readonly Button refresh;
        private readonly ScrollRect previewScroll;
        private readonly Action<MapEntry> selectedChanged;
        private readonly Func<MapEntry,ModRequisiteIssue[]> mapRequisites;
        private readonly Func<MapEntry,ModRequisite[]> declaredMapRequisites;
        private readonly Action<ModRequisiteIssue> requisiteAction;
        private readonly Dictionary<Button,MapEntry> rows=new Dictionary<Button,MapEntry>();
        private readonly Dictionary<Button,TMP_Text> rowNames=new Dictionary<Button,TMP_Text>(),rowErrors=new Dictionary<Button,TMP_Text>();
        private readonly List<RequisiteCard> requisiteCards=new List<RequisiteCard>();
        private readonly List<GameObject> decorations=new List<GameObject>();
        private readonly List<Button> linkButtons=new List<Button>();
        private MapEntry? selected;
        private bool busy;
        private Vector2 layoutSize;
        internal bool IsOpen=>root!=null&&root.activeSelf;

        internal NativeMapsUi(RectTransform parent,TMP_Text font,Action<MapEntry> selectedChanged,UnityAction refreshMaps,Func<MapEntry,ModRequisiteIssue[]> mapRequisites,Action<ModRequisiteIssue> requisiteAction,Func<MapEntry,ModRequisite[]> declaredMapRequisites)
        {
            this.parent=parent;this.font=font;this.selectedChanged=selectedChanged;
            this.mapRequisites=mapRequisites;this.requisiteAction=requisiteAction;this.declaredMapRequisites=declaredMapRequisites;
            var container=Rect("Maps",parent);Stretch(container);root=container.gameObject;root.SetActive(false);
            try
            {
                var list=Rect("Installed maps",container);list.anchorMin=Vector2.zero;list.anchorMax=new Vector2(.36f,1);list.offsetMin=new Vector2(24,152);list.offsetMax=new Vector2(-10,-112);listContent=Scroll(list);
                var preview=Rect("Map preview",container);preview.anchorMin=new Vector2(.36f,0);preview.anchorMax=Vector2.one;preview.offsetMin=new Vector2(10,152);preview.offsetMax=new Vector2(-24,-112);preview.gameObject.AddComponent<Image>().color=new Color(.12f,.15f,.17f,1);previewContent=Scroll(preview);previewScroll=preview.GetComponent<ScrollRect>();
                name=Text("Map name",previewContent,26);byline=Text("Author and version",previewContent,17);byline.color=LobbyListItem.TextMutedColor;state=Text("Map state",previewContent,17);state.color=LobbyListItem.TextMutedColor;
                pictureBox=Rect("Map image",previewContent);pictureBox.gameObject.AddComponent<Image>().color=new Color(.10f,.12f,.14f,1);
                var spriteRect=Rect("Borrowed sprite",pictureBox);Stretch(spriteRect);spriteImage=spriteRect.gameObject.AddComponent<Image>();spriteImage.raycastTarget=false;spriteImage.preserveAspect=true;
                var textureRect=Rect("Borrowed texture",pictureBox);Stretch(textureRect);textureImage=textureRect.gameObject.AddComponent<RawImage>();textureImage.raycastTarget=false;textureAspect=textureRect.gameObject.AddComponent<AspectRatioFitter>();textureAspect.aspectMode=AspectRatioFitter.AspectMode.FitInParent;
                previewHint=Text("Image status",previewContent,17);previewHint.color=LobbyListItem.TextMutedColor;
                description=Text("Description",previewContent,20);credits=Text("Credits",previewContent,18);credits.color=LobbyListItem.TextMutedColor;
                noticeBox=Rect("Map notice",previewContent);noticeBox.gameObject.AddComponent<Image>().color=new Color(.16f,.20f,.22f,1);notice=Text("Notice",noticeBox,18);notice.color=new Color(.92f,.80f,.32f,1);
                requisiteBox=Rect("Prerequisites",previewContent);requisiteBox.gameObject.AddComponent<Image>().color=new Color(.16f,.20f,.22f,1);requisiteTitle=Text("Prerequisites title",requisiteBox,19);requisiteTitle.text="Prerequisites";Top(requisiteTitle.rectTransform,12,10,-12,28);requisiteSummary=Text("Required mods",requisiteBox,17);requisiteSummary.color=LobbyListItem.TextMutedColor;requisiteBox.gameObject.SetActive(false);
                links=Rect("Map links",previewContent);
                var statusArea=Rect("Map status",container);Bottom(statusArea,24,64,-24,66);statusContent=Scroll(statusArea);status=Text("Status",statusContent,17);status.color=LobbyListItem.TextMutedColor;
                refresh=Button("Refresh maps",container,()=>{if(!busy)refreshMaps();});Bottom((RectTransform)refresh.transform,24,18,-210,40);var refreshRect=(RectTransform)refresh.transform;refreshRect.anchorMax=Vector2.zero;refreshRect.offsetMax=new Vector2(284,58);refresh.GetComponent<Image>().color=LobbyListItem.StatusSuccessColor;refresh.GetComponentInChildren<TMP_Text>(true).color=new Color(.08f,.12f,.10f,1);
                ClearImages();DrawPreview(null);
            }
            catch {UnityEngine.Object.Destroy(root);throw;}
        }

        internal void Show(){root.SetActive(true);Canvas.ForceUpdateCanvases();DrawPreview(selected);}
        internal void Hide(){if(root==null)return;ClearImages();root.SetActive(false);}
        internal void Set(IReadOnlyList<MapEntry> maps,string message)
        {
            string previous=selected?.FilePath??"";
            foreach(var row in rows.Keys){row.gameObject.SetActive(false);UnityEngine.Object.Destroy(row.gameObject);}rows.Clear();rowNames.Clear();rowErrors.Clear();foreach(var item in decorations)UnityEngine.Object.Destroy(item);decorations.Clear();
            float y=0;
            foreach(var item in maps.Where(m=>m!=null))
            {
                var map=item;var row=Button("",listContent,()=>{if(!busy)Select(map,true);});Top((RectTransform)row.transform,0,y,0,82);rows.Add(row,map);
                var title=row.GetComponentInChildren<TMP_Text>(true);title.text=Value(map.Name,"Unnamed map");title.fontSize=20;title.fontSizeMax=20;title.fontSizeMin=15;title.alignment=TextAlignmentOptions.MidlineLeft;Top(title.rectTransform,12,7,-12,32);
                rowNames.Add(row,title);
                var detail=Text("State",(RectTransform)row.transform,16);detail.text=(string.IsNullOrWhiteSpace(map.Version)?"":"Version "+map.Version+" | ")+Value(map.State,"Installed map");detail.color=LobbyListItem.TextMutedColor;detail.enableWordWrapping=false;detail.overflowMode=TextOverflowModes.Ellipsis;Top(detail.rectTransform,12,44,-12,26);y+=90;
                var error=Text("Prerequisite error",(RectTransform)row.transform,16);error.text="";Top(error.rectTransform,12,72,-12,36);error.gameObject.SetActive(false);rowErrors.Add(row,error);
            }
            if(rows.Count==0)
            {var empty=Text("No installed maps",listContent,18);empty.text="No installed maps found.";empty.color=LobbyListItem.TextMutedColor;Top(empty.rectTransform,12,12,-12,60);decorations.Add(empty.gameObject);y=84;}
            listContent.sizeDelta=new Vector2(0,y);if(!busy)status.text=message;
            var next=previous.Length==0?null:rows.Values.FirstOrDefault(m=>string.Equals(m.FilePath,previous,StringComparison.OrdinalIgnoreCase));
            Select(next??maps.FirstOrDefault(m=>m!=null),true);RefreshRequisites();Busy(busy);
        }
        internal void RefreshPreview(MapEntry item)
        {if(IsOpen){if(selected==item)DrawPreview(item);RefreshRequisites();}}
        internal void RefreshRequisites()
        {
            foreach(var row in rows)
            {
                var issues=mapRequisites(row.Value);var declared=declaredMapRequisites(row.Value);bool broken=issues.Length>0;var error=rowErrors[row.Key];
                rowNames[row.Key].color=broken?LobbyListItem.StatusDangerColor:Color.white;
                error.text=issues.Length==0?(declared.Length>0?"Dependencies ready":""):issues[0].Message+(issues.Length>1?" ("+(issues.Length-1)+" more; select for details.)":"");error.color=broken?LobbyListItem.StatusDangerColor:LobbyListItem.TextMutedColor;error.gameObject.SetActive(error.text.Length>0);
                row.Key.GetComponent<Image>().color=row.Value==selected?new Color(.20f,.34f,.27f,1):new Color(.12f,.15f,.17f,1);
            }
            DrawRequisites(selected);Layout();
        }
        internal void Busy(bool value)
        {busy=value;refresh.interactable=!value;foreach(var row in rows.Keys)row.interactable=!value;foreach(var link in linkButtons)link.interactable=!value;foreach(var card in requisiteCards)if(card.Action!=null)card.Action.interactable=!value;}
        internal void Status(string message){status.text=message;Layout();}
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
            DrawRequisites(item);
            Layout();
        }
        private void DrawRequisites(MapEntry? item)
        {
            foreach(var card in requisiteCards){card.Message.gameObject.SetActive(false);UnityEngine.Object.Destroy(card.Message.gameObject);if(card.Action!=null){card.Action.gameObject.SetActive(false);UnityEngine.Object.Destroy(card.Action.gameObject);}}requisiteCards.Clear();
            var issues=item==null?Array.Empty<ModRequisiteIssue>():mapRequisites(item);var declared=item==null?Array.Empty<ModRequisite>():declaredMapRequisites(item);requisiteBox.gameObject.SetActive(issues.Length>0||declared.Length>0);requisiteSummary.text=NativeModsUi.RequisiteSummary(declared);requisiteSummary.gameObject.SetActive(requisiteSummary.text.Length>0);
            foreach(var issue in issues)
            {
                var message=Text("Prerequisite message",requisiteBox,18);message.text=issue.Message;message.color=LobbyListItem.StatusDangerColor;
                string label=NativeModsUi.RequisiteActionLabel(issue);Button? action=null;
                if(label.Length>0){var captured=issue;action=Button(label,requisiteBox,()=>{if(!busy)requisiteAction(captured);});action.interactable=!busy;action.GetComponent<Image>().color=LobbyListItem.StatusSuccessColor;action.GetComponentInChildren<TMP_Text>(true).color=new Color(.08f,.12f,.10f,1);}
                requisiteCards.Add(new RequisiteCard(message,action));
            }
        }
        private float LayoutRequisites(float width)
        {
            float top=46;
            if(requisiteSummary.gameObject.activeSelf){float height=Measure(requisiteSummary,width,28);Top(requisiteSummary.rectTransform,12,top,-12,height);top+=height+8;}
            foreach(var card in requisiteCards)
            {float height=Measure(card.Message,width,32);Top(card.Message.rectTransform,12,top,-12,height);top+=height+8;if(card.Action!=null){Top((RectTransform)card.Action.transform,12,top,-12,38);top+=46;}}
            return top+4;
        }
        private sealed class RequisiteCard
        {
            internal readonly TMP_Text Message;
            internal readonly Button? Action;
            internal RequisiteCard(TMP_Text message,Button? action){Message=message;Action=action;}
        }
        private void ClearImages()
        {spriteImage.sprite=null;textureImage.texture=null;spriteImage.gameObject.SetActive(false);textureImage.gameObject.SetActive(false);pictureBox.gameObject.SetActive(false);}
        private void Layout()
        {
            LayoutRows();
            layoutSize=parent.rect.size;float width=Mathf.Max(1,previewContent.rect.width-36),top=16;
            top=Place(name,18,top,-18,width,40)+6;top=Place(byline,18,top,-18,width,24)+8;top=Place(state,18,top,-18,width,24)+14;
            if(pictureBox.gameObject.activeSelf){float height=Mathf.Min(280,width*.55f);Top(pictureBox,18,top,-18,height);top+=height+18;}
            if(previewHint.gameObject.activeSelf)top=Place(previewHint,18,top,-18,width,24)+14;
            top=Place(description,18,top,-18,width,60)+18;
            if(requisiteBox.gameObject.activeSelf){float height=LayoutRequisites(width-24);Top(requisiteBox,18,top,-18,height);top+=height+16;}
            if(credits.text.Length>0)top=Place(credits,18,top,-18,width,44)+18;
            if(noticeBox.gameObject.activeSelf)
            {float height=Measure(notice,width-24,40);Top(noticeBox,18,top,-18,height+24);Top(notice.rectTransform,12,12,-12,height);top+=height+42;}
            
            int columns=width>=520?2:1;int linkRows=(linkButtons.Count+columns-1)/columns;Top(links,18,top,-18,linkRows*46);for(int i=0;i<linkButtons.Count;i++)NativeMenuStyle.Cell((RectTransform)linkButtons[i].transform,i%columns,columns,(i/columns)*46,38,0);previewContent.sizeDelta=new Vector2(0,top+linkRows*46+18);
            float statusWidth=Mathf.Max(1,statusContent.rect.width),statusHeight=Measure(status,statusWidth,34);Top(status.rectTransform,0,0,0,statusHeight);statusContent.sizeDelta=new Vector2(0,statusHeight);
        }
        private void LayoutRows()
        {
            float y=0;
            foreach(var row in rows)
            {
                var error=rowErrors[row.Key];bool hasError=!string.IsNullOrEmpty(error.text);float errorHeight=!hasError?0:Mathf.Max(28,error.GetPreferredValues(error.text,Mathf.Max(1,listContent.rect.width-24),0).y+8);if(hasError)Top(error.rectTransform,12,72,-12,errorHeight);
                float height=!hasError?82:80+errorHeight;Top((RectTransform)row.Key.transform,0,y,0,height);y+=height+8;
            }
            if(rows.Count>0)listContent.sizeDelta=new Vector2(0,y);
        }
        private static string Value(string? value,string missing)=>string.IsNullOrWhiteSpace(value)?missing:value!;
        private static float Measure(TMP_Text text,float width,float minimum)=>Mathf.Max(minimum,text.GetPreferredValues(text.text,width,0).y+8);
        private static float Place(TMP_Text text,float left,float top,float right,float width,float minimum)
        {float height=Measure(text,width,minimum);Top(text.rectTransform,left,top,right,height);return top+height;}
        private TMP_Text Text(string name,RectTransform parent,float size)
        {var text=Rect(name,parent).gameObject.AddComponent<TextMeshProUGUI>();text.font=font.font;text.fontSharedMaterial=font.fontSharedMaterial;text.fontSize=size;text.color=Color.white;text.richText=false;text.raycastTarget=false;text.enableWordWrapping=true;text.overflowMode=TextOverflowModes.Overflow;return text;}
        private Button Button(string label,RectTransform parent,UnityAction action)
        {var rect=Rect(label,parent);var fill=rect.gameObject.AddComponent<Image>();fill.color=new Color(.20f,.24f,.26f,1);var button=rect.gameObject.AddComponent<Button>();button.targetGraphic=fill;NativeMenuStyle.Button(button);button.onClick.AddListener(action);var nav=button.navigation;nav.mode=Navigation.Mode.None;button.navigation=nav;var text=Text("Label",rect,18);Stretch(text.rectTransform,6);text.text=label;text.alignment=TextAlignmentOptions.Center;text.enableWordWrapping=false;text.overflowMode=TextOverflowModes.Ellipsis;text.enableAutoSizing=true;text.fontSizeMin=14;text.fontSizeMax=18;return button;}
        private static RectTransform Scroll(RectTransform parent)
        {var viewport=Rect("Viewport",parent);Stretch(viewport);viewport.gameObject.AddComponent<RectMask2D>();var hit=viewport.gameObject.AddComponent<Image>();hit.color=Color.clear;hit.canvasRenderer.cullTransparentMesh=false;var content=Rect("Content",viewport);content.anchorMin=new Vector2(0,1);content.anchorMax=Vector2.one;content.pivot=new Vector2(.5f,1);content.offsetMin=content.offsetMax=Vector2.zero;var scroll=parent.gameObject.AddComponent<ScrollRect>();scroll.horizontal=false;scroll.viewport=viewport;scroll.content=content;scroll.movementType=ScrollRect.MovementType.Clamped;scroll.scrollSensitivity=40;NativeScrollbars.Attach(scroll);return content;}
        private static RectTransform Rect(string name,Transform parent){var rect=(RectTransform)new GameObject(name,typeof(RectTransform)).transform;rect.SetParent(parent,false);return rect;}
        private static void Stretch(RectTransform rect,float inset=0){rect.anchorMin=Vector2.zero;rect.anchorMax=Vector2.one;rect.offsetMin=new Vector2(inset,inset);rect.offsetMax=new Vector2(-inset,-inset);}
        private static void Top(RectTransform rect,float left,float top,float right,float height){rect.anchorMin=new Vector2(0,1);rect.anchorMax=Vector2.one;rect.pivot=new Vector2(.5f,1);rect.offsetMin=new Vector2(left,-top-height);rect.offsetMax=new Vector2(right,-top);}
        private static void Bottom(RectTransform rect,float left,float bottom,float right,float height){rect.anchorMin=Vector2.zero;rect.anchorMax=new Vector2(1,0);rect.offsetMin=new Vector2(left,bottom);rect.offsetMax=new Vector2(right,bottom+height);}
        public void Dispose(){Hide();UnityEngine.Object.Destroy(root);}
    }
}
