using TMPro;
using UnityEngine;
using UnityEngine.Events;

/// <summary>Small shared controls matching the existing settings theme.</summary>
public static class PetSettingsWidgets
{
    public static readonly Color Field = new Color32(36, 47, 63, 255);
    public static readonly Color Ink = new Color32(238, 244, 251, 255);
    public static readonly Color Accent = new Color32(147, 232, 205, 255);
    public static RectTransform Rect(string name, Transform parent)
    { var go = new GameObject(name, typeof(RectTransform)); var rect = (RectTransform)go.transform; rect.SetParent(parent, false); return rect; }
    public static RectTransform Box(string name, Transform parent, Color color)
    { var rect = Rect(name,parent); var image = rect.gameObject.AddComponent<UnityEngine.UI.Image>(); image.color=color; image.sprite=Resources.Load<Sprite>("DesktopPetUIRound"); image.type=UnityEngine.UI.Image.Type.Sliced; return rect; }
    public static void Height(RectTransform rect,float value)
    { var l=rect.GetComponent<UnityEngine.UI.LayoutElement>()??rect.gameObject.AddComponent<UnityEngine.UI.LayoutElement>(); l.minHeight=l.preferredHeight=value; }
    public static void Stretch(RectTransform rect,float inset=0)
    { rect.anchorMin=Vector2.zero;rect.anchorMax=Vector2.one;rect.offsetMin=Vector2.one*inset;rect.offsetMax=-Vector2.one*inset; }
    public static void Vertical(RectTransform rect, int padding = 0)
    { var l=rect.gameObject.AddComponent<UnityEngine.UI.VerticalLayoutGroup>();l.padding=new RectOffset(padding,padding,padding,padding);l.spacing=8;l.childControlWidth=l.childControlHeight=true;l.childForceExpandWidth=true;l.childForceExpandHeight=false; }
    public static RectTransform Row(string name, Transform parent,float height=40)
    { var rect=Rect(name,parent);Height(rect,height);var l=rect.gameObject.AddComponent<UnityEngine.UI.HorizontalLayoutGroup>();l.spacing=8;l.childControlWidth=l.childControlHeight=true;l.childForceExpandWidth=true;l.childForceExpandHeight=false;return rect; }
    public static TMP_Text Label(Transform parent,string text,float size=15,float minHeight=25)
    { var rt=Rect("Label",parent);var label=rt.gameObject.AddComponent<TextMeshProUGUI>();label.font=Resources.Load<TMP_FontAsset>("DesktopPetUIFont");label.fontSize=size;label.text=text;label.richText=false;label.color=Ink;label.raycastTarget=false;Height(rt,minHeight);rt.GetComponent<UnityEngine.UI.LayoutElement>().preferredHeight=-1;return label; }
    public static UnityEngine.UI.Button Button(Transform parent,string caption,UnityAction action,bool primary=false)
    { var rt=Box(caption,parent,primary?Accent:Field);Height(rt,40);var l=rt.GetComponent<UnityEngine.UI.LayoutElement>();l.minWidth=60;l.flexibleWidth=1;var button=rt.gameObject.AddComponent<UnityEngine.UI.Button>();button.targetGraphic=rt.GetComponent<UnityEngine.UI.Image>();button.onClick.AddListener(action);var label=Label(rt,caption,14);Stretch(label.rectTransform,6);label.color=primary?new Color32(17,48,41,255):Ink;label.alignment=TextAlignmentOptions.Center;return button; }
    public static TMP_InputField Input(Transform parent,string caption,string hint,int limit=240,int height=42,bool multiline=false)
    { Label(parent,caption,14,22);var rt=Box(caption,parent,Field);Height(rt,height);var input=rt.gameObject.AddComponent<TMP_InputField>();input.targetGraphic=rt.GetComponent<UnityEngine.UI.Image>();var viewport=Rect("Viewport",rt);Stretch(viewport,9);viewport.gameObject.AddComponent<UnityEngine.UI.RectMask2D>();var label=Label(viewport,"",16);Stretch(label.rectTransform);var placeholder=Label(viewport,hint,15);Stretch(placeholder.rectTransform);input.textViewport=viewport;input.textComponent=label;input.placeholder=placeholder;input.fontAsset=label.font;input.characterLimit=limit;input.lineType=multiline?TMP_InputField.LineType.MultiLineNewline:TMP_InputField.LineType.SingleLine;input.customCaretColor=true;input.caretColor=Accent;PetInputPlaceholder.Install(input);return input; }
}
