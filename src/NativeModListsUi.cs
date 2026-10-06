using System;
using System.Collections.Generic;
using System.Linq;
using NuclearOption.Networking.Lobbies;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace KellysJOINCHECK
{
    internal sealed class NativeModListsUi
    {
        private readonly RectTransform parent,dialog,body,bodyContent,listPane,listContent;
        private readonly GameObject root;
        private readonly TMP_Text font,nameLabel,summary,status;
        private readonly TMP_InputField nameInput;
        private readonly Button save,load,delete,back;
        private readonly ScrollRect listScroll;
        private readonly Dictionary<Button,SavedModList> rows=new Dictionary<Button,SavedModList>();
        private readonly List<GameObject> decorations=new List<GameObject>();
        private SavedModList? selected;
        private bool busy,wasTyping;
        private Vector2 layoutSize;
        internal bool IsOpen=>root!=null&&root.activeSelf;

        internal NativeModListsUi(RectTransform parent,TMP_Text font,Action<string> saveList,Action<SavedModList> loadList,Action<SavedModList> deleteList,UnityAction close)
        {
            this.parent=parent;this.font=font;
            var shade=Rect("Saved mod lists",parent);root=shade.gameObject;root.SetActive(false);Stretch(shade);shade.gameObject.AddComponent<Image>().color=new Color(0,0,0,.78f);
            try
            {
                dialog=Rect("Saved mod lists panel",shade);dialog.anchorMin=dialog.anchorMax=dialog.pivot=new Vector2(.5f,.5f);dialog.gameObject.AddComponent<Image>().color=new Color(.16f,.20f,.22f,1);
                var outline=dialog.gameObject.AddComponent<Outline>();outline.effectColor=new Color(.36f,.42f,.46f,1);outline.effectDistance=new Vector2(1,-1);
                var title=Text("Title",dialog,26);title.text="SAVED MOD LISTS";Top(title.rectTransform,24,18,-24,40);
                var help=Text("Help",dialog,17);help.text="Choose a list, or name the mods you have ticked.";help.color=LobbyListItem.TextMutedColor;Top(help.rectTransform,24,62,-24,42);
                body=Rect("Scrollable form",dialog);bodyContent=Scroll(body);
                listPane=Rect("Existing lists",bodyContent);listPane.gameObject.AddComponent<Image>().color=new Color(.12f,.15f,.17f,1);listContent=Scroll(listPane);listScroll=listPane.GetComponent<ScrollRect>();
                nameLabel=Text("Name label",bodyContent,17);nameLabel.text="List name";nameLabel.color=LobbyListItem.TextMutedColor;
                var inputRect=Rect("List name",bodyContent);inputRect.gameObject.SetActive(false);var fill=inputRect.gameObject.AddComponent<Image>();fill.color=new Color(.10f,.13f,.15f,1);
                var viewport=Rect("Text area",inputRect);Stretch(viewport,10);viewport.gameObject.AddComponent<RectMask2D>();
                var inputText=Text("Text",viewport,20);Stretch(inputText.rectTransform);inputText.alignment=TextAlignmentOptions.MidlineLeft;inputText.enableWordWrapping=false;inputText.overflowMode=TextOverflowModes.Overflow;
                var placeholder=Text("Placeholder",viewport,18);Stretch(placeholder.rectTransform);placeholder.text="For example: Shipyard or Vanilla";placeholder.alignment=TextAlignmentOptions.MidlineLeft;placeholder.color=LobbyListItem.TextMutedColor;placeholder.enableWordWrapping=false;
                nameInput=inputRect.gameObject.AddComponent<TMP_InputField>();nameInput.targetGraphic=fill;nameInput.textViewport=viewport;nameInput.textComponent=inputText;nameInput.placeholder=placeholder;nameInput.lineType=TMP_InputField.LineType.SingleLine;nameInput.characterLimit=80;nameInput.caretWidth=2;nameInput.customCaretColor=true;nameInput.caretColor=Color.white;nameInput.selectionColor=new Color(.25f,.80f,.48f,.35f);var navigation=nameInput.navigation;navigation.mode=Navigation.Mode.None;nameInput.navigation=navigation;nameInput.onValueChanged.AddListener(_=>RefreshButtons());inputRect.gameObject.SetActive(true);
                summary=Text("Selected list",bodyContent,17);summary.color=LobbyListItem.TextMutedColor;summary.overflowMode=TextOverflowModes.Overflow;
                status=Text("Status",bodyContent,17);status.color=LobbyListItem.TextMutedColor;status.overflowMode=TextOverflowModes.Overflow;
                save=Button("Save selection",dialog,()=>{if(!busy&&nameInput.text.Trim().Length>0)saveList(nameInput.text.Trim());});save.GetComponent<Image>().color=LobbyListItem.StatusSuccessColor;save.GetComponentInChildren<TMP_Text>(true).color=new Color(.08f,.12f,.10f,1);
                load=Button("Load list",dialog,()=>{if(!busy&&selected!=null)loadList(selected);});
                delete=Button("Delete",dialog,()=>{if(!busy&&selected!=null)deleteList(selected);});delete.GetComponent<Image>().color=new Color(.38f,.14f,.16f,1);
                back=Button("Back",dialog,()=>{if(!busy)close();});
            }
            catch {UnityEngine.Object.Destroy(root);throw;}
        }

        internal void Show(IReadOnlyList<SavedModList> lists,string message)
        {
            bool wasOpen=IsOpen;string previous=selected?.FilePath??"",entered=nameInput.text;float scroll=listScroll.verticalNormalizedPosition;
            foreach(var row in rows.Keys)UnityEngine.Object.Destroy(row.gameObject);rows.Clear();foreach(var item in decorations)UnityEngine.Object.Destroy(item);decorations.Clear();
            float y=0;
            foreach(var item in lists)
            {
                var list=item;var row=Button("",listContent,()=>{if(!busy)Select(list,true);});Top((RectTransform)row.transform,0,y,0,68);rows.Add(row,list);
                var label=row.GetComponentInChildren<TMP_Text>(true);label.text=list.Name;label.fontSize=20;label.fontSizeMax=20;label.fontSizeMin=16;label.alignment=TextAlignmentOptions.MidlineLeft;Top(label.rectTransform,12,5,-12,30);
                var detail=Text("List summary",(RectTransform)row.transform,16);detail.text=Byline(list);detail.color=LobbyListItem.TextMutedColor;detail.enableWordWrapping=false;Top(detail.rectTransform,12,38,-12,24);y+=76;
            }
            if(lists.Count==0)
            {var empty=Text("No saved lists",listContent,18);empty.text="No saved lists yet.";empty.color=LobbyListItem.TextMutedColor;Top(empty.rectTransform,12,12,-12,40);decorations.Add(empty.gameObject);y=64;}
            listContent.sizeDelta=new Vector2(0,y);
            selected=previous.Length==0?null:lists.FirstOrDefault(l=>string.Equals(l.FilePath,previous,StringComparison.OrdinalIgnoreCase));
            Select(selected,!wasOpen||selected==null&&previous.Length>0);
            if(wasOpen&&selected!=null)nameInput.SetTextWithoutNotify(entered);
            root.SetActive(true);root.transform.SetAsLastSibling();Status(message.Length>0?message:lists.Count==0?"Save your current ticks here. Loading a list may need a restart.":"Loading a list changes your selection. DOORMAN will explain any missing mods or restart.");
            Canvas.ForceUpdateCanvases();Layout();listScroll.verticalNormalizedPosition=wasOpen?scroll:1;RefreshButtons();
        }

        internal void Hide(){StopTyping();root.SetActive(false);}
        internal void Status(string message){status.text=message;if(IsOpen)Layout();}
        internal void Busy(bool value){busy=value;if(value)StopTyping();nameInput.interactable=!value;foreach(var row in rows.Keys)row.interactable=!value;RefreshButtons();}
        internal void Tick(){wasTyping=nameInput.isFocused;if(parent.rect.size!=layoutSize)Layout();}
        internal bool EscapeTyping(){if(!nameInput.isFocused&&!wasTyping)return false;StopTyping();return true;}
        internal void StopTyping()
        {
            nameInput.DeactivateInputField();wasTyping=false;
            if(EventSystem.current!=null&&EventSystem.current.currentSelectedGameObject==nameInput.gameObject)EventSystem.current.SetSelectedGameObject(null);
        }

        private void Select(SavedModList? list,bool setName)
        {
            selected=list;if(setName)nameInput.SetTextWithoutNotify(list?.Name??"");
            summary.text=list==null?"Save the current ticks as a named mod list.":"Selected: "+list.Name+"\n"+Byline(list);
            foreach(var row in rows)row.Key.GetComponent<Image>().color=row.Value==list?new Color(.20f,.34f,.27f,1):new Color(.20f,.24f,.26f,1);
            RefreshButtons();if(IsOpen)Layout();
        }
        private static string Byline(SavedModList list)=>"Game "+list.GameVersion+" | "+(list.Mods?.Count(m=>m.Enabled)??0)+" enabled / "+(list.Mods?.Length??0)+" saved";
        private void RefreshButtons()
        {if(save==null)return;save.interactable=!busy&&nameInput.text.Trim().Length>0;load.interactable=delete.interactable=!busy&&selected!=null;back.interactable=!busy;}
        private void Layout()
        {
            layoutSize=parent.rect.size;float width=Mathf.Min(820,Mathf.Max(300,parent.rect.width-48)),height=Mathf.Min(650,Mathf.Max(300,parent.rect.height-48));dialog.sizeDelta=new Vector2(width,height);
            int columns=width>=640?4:2;float footer=columns==4?84:132;body.anchorMin=Vector2.zero;body.anchorMax=Vector2.one;body.offsetMin=new Vector2(24,footer);body.offsetMax=new Vector2(-24,-112);
            float contentWidth=Mathf.Max(1,bodyContent.rect.width),summaryHeight=Mathf.Max(40,summary.GetPreferredValues(summary.text,contentWidth,0).y+8),statusHeight=Mathf.Max(44,status.GetPreferredValues(status.text,contentWidth,0).y+8);
            float listHeight=Mathf.Clamp(height-112-footer-174-summaryHeight-statusHeight,100,280),top=listHeight+16;Top(listPane,0,0,0,listHeight);Top(nameLabel.rectTransform,0,top,0,24);top+=30;Top((RectTransform)nameInput.transform,0,top,0,44);top+=58;Top(summary.rectTransform,0,top,0,summaryHeight);top+=summaryHeight+12;Top(status.rectTransform,0,top,0,statusHeight);bodyContent.sizeDelta=new Vector2(0,top+statusHeight+12);
            var buttons=new[]{save,load,delete,back};float buttonWidth=(width-48-(columns-1)*10)/columns;
            for(int i=0;i<buttons.Length;i++)
            {var rect=(RectTransform)buttons[i].transform;rect.anchorMin=rect.anchorMax=rect.pivot=Vector2.zero;rect.sizeDelta=new Vector2(buttonWidth,40);rect.anchoredPosition=new Vector2(24+(i%columns)*(buttonWidth+10),columns==4?24:24+(1-i/columns)*48);}
        }

        private TMP_Text Text(string name,RectTransform parent,float size)
        {var text=Rect(name,parent).gameObject.AddComponent<TextMeshProUGUI>();text.font=font.font;text.fontSharedMaterial=font.fontSharedMaterial;text.fontSize=size;text.color=Color.white;text.richText=false;text.raycastTarget=false;text.enableWordWrapping=true;text.overflowMode=TextOverflowModes.Ellipsis;return text;}
        private Button Button(string label,RectTransform parent,UnityAction action)
        {var rect=Rect(label,parent);var fill=rect.gameObject.AddComponent<Image>();fill.color=new Color(.20f,.24f,.26f,1);var button=rect.gameObject.AddComponent<Button>();button.targetGraphic=fill;button.onClick.AddListener(action);var nav=button.navigation;nav.mode=Navigation.Mode.None;button.navigation=nav;var text=Text("Label",rect,18);Stretch(text.rectTransform,6);text.text=label;text.alignment=TextAlignmentOptions.Center;text.enableWordWrapping=false;text.enableAutoSizing=true;text.fontSizeMin=14;text.fontSizeMax=18;return button;}
        private static RectTransform Scroll(RectTransform parent)
        {var viewport=Rect("Viewport",parent);Stretch(viewport);viewport.gameObject.AddComponent<RectMask2D>();var hit=viewport.gameObject.AddComponent<Image>();hit.color=Color.clear;hit.canvasRenderer.cullTransparentMesh=false;var content=Rect("Content",viewport);content.anchorMin=new Vector2(0,1);content.anchorMax=Vector2.one;content.pivot=new Vector2(.5f,1);content.offsetMin=content.offsetMax=Vector2.zero;var scroll=parent.gameObject.AddComponent<ScrollRect>();scroll.horizontal=false;scroll.viewport=viewport;scroll.content=content;scroll.movementType=ScrollRect.MovementType.Clamped;scroll.scrollSensitivity=40;NativeScrollbars.Attach(scroll);return content;}
        private static RectTransform Rect(string name,Transform parent){var rect=(RectTransform)new GameObject(name,typeof(RectTransform)).transform;rect.SetParent(parent,false);return rect;}
        private static void Stretch(RectTransform rect,float inset=0){rect.anchorMin=Vector2.zero;rect.anchorMax=Vector2.one;rect.offsetMin=new Vector2(inset,inset);rect.offsetMax=new Vector2(-inset,-inset);}
        private static void Top(RectTransform rect,float left,float top,float right,float height){rect.anchorMin=new Vector2(0,1);rect.anchorMax=Vector2.one;rect.pivot=new Vector2(.5f,1);rect.offsetMin=new Vector2(left,-top-height);rect.offsetMax=new Vector2(right,-top);}
    }
}
