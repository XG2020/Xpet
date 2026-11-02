using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using static PetSettingsWidgets;

public sealed class PetCharacterCardsPanel : MonoBehaviour
{
    private PetCharacterCards service;
    private RectTransform grid, skinList;
    private TMP_Text status, editorTitle, modelLabel;
    private TMP_InputField nameInput, personaInput, skinNameInput;
    private TMP_Text skinSummary, aiSkinLabel;
    private string selectedSkinId;
    private readonly Dictionary<string,TMP_InputField> voice = new Dictionary<string,TMP_InputField>();
    private readonly List<UnityEngine.UI.Button> controls = new List<UnityEngine.UI.Button>();
    private PetCharacterCard draft;
    private bool newCard, built;
    private string rendered;
    private string selectedModel;
    private TMP_Dropdown modelPicker;
    private string[] modelIds=Array.Empty<string>();
    public void PrepareLayout() { if (!built) Build(); }
    private void OnEnable()
    {
        service = FindFirstObjectByType<PetCharacterCards>(FindObjectsInactive.Include);
        if (!built) Build();
        if (service != null) { service.Changed += Refresh; service.Saved += Edit; service.Refresh(); }
        Refresh();
    }
    private void OnDisable() { if (service != null) { service.Changed -= Refresh; service.Saved -= Edit; } }
    private void Build()
    {
        built=true;
        Label(transform,"角色卡",22,32);
        Label(transform,"为每个人物保存名称、人设、声音、模型与动作。切换后使用该角色自己的聊天记录和记忆。",14,48);
        var top=Row("CardToolbar",transform);
        controls.Add(Button(top,"新建角色",NewCard,true));
        controls.Add(Button(top,"刷新列表",()=>service?.Refresh()));
        grid=Rect("CharacterGrid",transform);
        var layout=grid.gameObject.AddComponent<UnityEngine.UI.GridLayoutGroup>();layout.spacing=new Vector2(10,10);layout.constraint=UnityEngine.UI.GridLayoutGroup.Constraint.FixedColumnCount;
        Height(grid,160);
        var gridSize=grid.GetComponent<UnityEngine.UI.LayoutElement>();gridSize.minWidth=gridSize.preferredWidth=0;gridSize.flexibleWidth=1;
        status=Label(transform,"",14,56);status.color=Accent;
        editorTitle=Label(transform,"角色详情",19,28);
        nameInput=Input(transform,"人物名称","例如：丽塔",64);
        personaInput=Input(transform,"人设提示词","身份、说话风格、与用户的关系…",8000,140,true);
        Label(transform,"服饰皮肤",19,28);
        Label(transform,"把同一角色的不同服饰模型加入这张卡。换装保留人设、声音、动作与聊天记忆。",14,48);
        skinList=Rect("CharacterSkins",transform);Vertical(skinList);Height(skinList,48);
        skinSummary=Label(transform,"",14,28);
        skinNameInput=Input(transform,"皮肤名称","例如：日常服饰 / 晚礼服",48);
        modelLabel=Label(transform,"",14,36);
        var modelPage=FindFirstObjectByType<PetModelSettingsPanel>(FindObjectsInactive.Include);
        var template=modelPage!=null?modelPage.GetComponentInChildren<TMP_Dropdown>(true):null;
        if(template!=null)
        {
            modelPicker=Instantiate(template,transform);modelPicker.name="CardModelPicker";
            modelPicker.onValueChanged=new TMP_Dropdown.DropdownEvent();
            modelPicker.onValueChanged.AddListener(index=>{if(index>=0&&index<modelIds.Length){selectedModel=modelIds[index];ShowModel();}});
        }
        else controls.Add(Button(transform,"选择下一个模型",CycleModel));
        var skinEdit=Row("SkinEditActions",transform);
        controls.Add(Button(skinEdit,"添加皮肤",AddSkin));
        controls.Add(Button(skinEdit,"删除此皮肤",DeleteSkin));
        var skinUse=Row("SkinUseActions",transform);
        controls.Add(Button(skinUse,"设为当前服饰",SelectActiveSkin));
        controls.Add(Button(skinUse,"穿戴已保存服饰",WearSkin,true));
        var aiSkinButton=Button(transform,"",ToggleAiSkins);
        aiSkinLabel=aiSkinButton.GetComponentInChildren<TMP_Text>();controls.Add(aiSkinButton);
        Label(transform,"新增、改名、模型绑定和当前服饰选择，点击底部“保存角色卡”生效。自动换装只使用此卡已保存的皮肤；模型可先在“模型导入”页准备。",14,64);
        Label(transform,"动作库使用该角色保存的组合。编辑当前角色的动作，请进入“动作管理”。",14,48);
        var actions=Row("CardActions",transform);
        controls.Add(Button(actions,"管理动作",()=>FindFirstObjectByType<SettingsTabsController>(FindObjectsInactive.Include)?.ShowPage("动作管理")));
        controls.Add(Button(actions,"复制当前动作到此卡",()=>{if(draft!=null){draft.actions=PetActionLibrary.Export();status.text="已复制当前动作库，保存角色卡后生效。";}}));
        Voice("tts_provider","语音服务（edge / openai / gpt_sovits）","edge");
        Voice("default_tts_voice","Edge 音色","zh-CN-XiaoxiaoNeural");
        Voice("tts_base_url","TTS 接口地址","留空沿用聊天服务地址");
        Voice("tts_model","TTS 模型","gpt-4o-mini-tts");
        Voice("tts_voice","TTS 音色 / Voice ID","alloy");
        Voice("tts_api_key","TTS 密钥（留空保留）","不回显已保存的密钥",true);
        Voice("asr_base_url","语音识别接口地址","留空沿用聊天服务地址");
        Voice("asr_model","语音识别模型","whisper-1");
        Voice("asr_api_key","语音识别密钥（留空保留）","不回显已保存的密钥",true);
        Voice("gpt_sovits_ref_audio_path","GPT-SoVITS 参考音频路径","语音服务器可访问的路径");
        Voice("gpt_sovits_prompt_text","GPT-SoVITS 参考音频文本","");
        Voice("gpt_sovits_prompt_lang","参考音频语言","zh");
        Voice("gpt_sovits_text_lang","输出语言","zh");
        var footer=Row("CardEditorActions",transform);
        controls.Add(Button(footer,"保存角色卡",Save,true));
        controls.Add(Button(footer,"另存为新角色",SaveCopy));
        Label(transform,"默认角色不可删除。语音密钥只保存在本机，角色卡列表不会返回密钥明文。",13,42);
    }
    private void Voice(string key,string caption,string hint,bool secret=false)
    { var input=Input(transform,caption,hint,key.Contains("path")||key.Contains("text")?4000:2048);if(secret)input.contentType=TMP_InputField.ContentType.Password;voice[key]=input; }
    private void LateUpdate()
    {
        if(grid==null)return;
        float width=((RectTransform)transform).rect.width;
        int columns=Mathf.Clamp(Mathf.FloorToInt((width+10)/235),1,4);
        var layout=grid.GetComponent<UnityEngine.UI.GridLayoutGroup>();layout.constraintCount=columns;layout.cellSize=new Vector2(Mathf.Max(180,(width-10*(columns-1))/columns),142);
        Height(grid,Mathf.Max(1,Mathf.CeilToInt(grid.childCount/(float)columns))*152-10);
    }
    private void Refresh()
    {
        if(!built||service==null)return;
        status.text=service.Status;
        foreach(var button in controls)if(button!=null)button.interactable=!service.IsBusy;
        if(modelPicker!=null)modelPicker.interactable=!service.IsBusy;
        if(skinList!=null)foreach(var button in skinList.GetComponentsInChildren<UnityEngine.UI.Button>())button.interactable=!service.IsBusy;
        string signature=service.ActiveId+"|"+JsonUtility.ToJson(new PetCharacterCardList{cards=service.Cards});
        if(rendered==signature){if(draft==null&&!service.IsBusy&&service.Cards.Length>0)Edit(service.Cards.FirstOrDefault(c=>c.id==service.ActiveId)??service.Cards[0]);return;}rendered=signature;
        foreach(Transform child in grid) {child.gameObject.SetActive(false);Destroy(child.gameObject);}
        foreach(var card in service.Cards)
        {
            var copy=card;var box=Box("Card_"+card.id,grid,card.id==service.ActiveId?new Color32(40,74,68,255):Field);Vertical(box,12);
            Label(box,card.name+(card.id==service.ActiveId?" · 使用中":""),19,26);
            var detail=Label(box,(card.persona??"").Replace('\n',' '),13,33);detail.maxVisibleLines=2;detail.overflowMode=TextOverflowModes.Ellipsis;
            var row=Row("Actions",box,36);
            Button(row,"编辑",()=>Edit(copy));
            Button(row,"切换",()=>service.Activate(copy),true);
            if(card.id!="default"&&card.id!=service.ActiveId)Button(row,"删除",()=>service.Delete(copy.id));
        }
        if(draft==null&&!service.IsBusy&&service.Cards.Length>0)Edit(service.Cards.FirstOrDefault(c=>c.id==service.ActiveId)??service.Cards[0]);
    }
    private void Edit(PetCharacterCard card)
    {
        if(service.IsBusy)return;
        draft=JsonUtility.FromJson<PetCharacterCard>(JsonUtility.ToJson(card));newCard=false;
        PetCharacterCards.EnsureSkins(draft);
        editorTitle.text="编辑 · "+draft.name;
        nameInput.SetTextWithoutNotify(draft.name);personaInput.SetTextWithoutNotify(draft.persona);
        selectedSkinId=draft.active_skin_id;ShowSkin();
        foreach(var pair in voice)pair.Value.SetTextWithoutNotify((string)typeof(PetCharacterCard).GetField(pair.Key).GetValue(draft)??"");
        status.text="正在编辑“"+draft.name+"”。TTS 密钥："+(draft.tts_api_key_configured?"已保存":"未设置")+"；ASR 密钥："+(draft.asr_api_key_configured?"已保存":"未设置");
    }
    private void NewCard()
    {
        if(service==null||service.IsBusy)return;
        Edit(new PetCharacterCard{name="新角色",persona="你是一位陪伴用户的桌面伙伴。请自然地用第一人称回应。",model_id=PetModelLibrary.BuiltinModelId});
        newCard=true;editorTitle.text="新建角色卡";
    }
    private void ShowModel()
    { var library=FindFirstObjectByType<PetModelLibrary>();modelLabel.text="此皮肤的模型："+(library?.ModelEntries.FirstOrDefault(m=>m.id==selectedModel)?.label??selectedModel);
        if(modelPicker!=null&&library!=null){modelIds=library.ModelEntries.Select(m=>m.id).ToArray();modelPicker.ClearOptions();modelPicker.AddOptions(library.ModelEntries.Select(m=>m.label).ToList());modelPicker.SetValueWithoutNotify(Mathf.Max(0,Array.IndexOf(modelIds,selectedModel)));modelPicker.RefreshShownValue();}
    }
    private void CycleModel()
    { var library=FindFirstObjectByType<PetModelLibrary>();if(library==null||library.ModelEntries.Count==0)return;var ids=library.ModelEntries.Select(m=>m.id).ToArray();selectedModel=ids[(Array.IndexOf(ids,selectedModel)+1)%ids.Length];ShowModel(); }
    private PetCharacterSkin EditingSkin => draft?.skins?.FirstOrDefault(skin=>skin!=null&&skin.id==selectedSkinId);
    private bool StoreSkinEdit()
    {
        var skin=EditingSkin;if(skin==null)return false;
        if(string.IsNullOrWhiteSpace(skinNameInput.text)){status.text="请填写皮肤名称。";return false;}
        skin.name=skinNameInput.text.Trim();skin.model_id=selectedModel;return true;
    }
    private void ShowSkin()
    {
        var skin=EditingSkin;if(skin==null)return;
        skinNameInput.SetTextWithoutNotify(skin.name);selectedModel=string.IsNullOrEmpty(skin.model_id)?PetModelLibrary.BuiltinModelId:skin.model_id;ShowModel();RenderSkins();
    }
    private void RenderSkins()
    {
        foreach(Transform child in skinList){child.gameObject.SetActive(false);Destroy(child.gameObject);}
        foreach(var skin in draft.skins)
        {
            var item=skin;var row=Row("Skin_"+skin.id,skinList,40);
            var label=Label(row,skin.name+(skin.id==draft.active_skin_id?" · 当前服饰":"")+(skin.id==selectedSkinId?" · 编辑中":""),14,32);
            label.overflowMode=TextOverflowModes.Ellipsis;
            var size=label.GetComponent<UnityEngine.UI.LayoutElement>();size.minWidth=0;size.flexibleWidth=3;
            Button(row,"编辑",()=>{if(service.IsBusy||!StoreSkinEdit())return;selectedSkinId=item.id;ShowSkin();});
        }
        Height(skinList,Mathf.Max(1,draft.skins.Length)*48-8);
        skinSummary.text="共 "+draft.skins.Length+" 套皮肤；正在编辑："+(EditingSkin?.name??"");
        aiSkinLabel.text="大模型自动换装："+(draft.allow_ai_skin_switch?"允许":"关闭")+"（点击切换）";
    }
    private void AddSkin()
    {
        if(draft==null||service.IsBusy||!StoreSkinEdit())return;
        if(draft.skins.Length>=32){status.text="每张角色卡最多保存 32 套皮肤。";return;}
        var skin=new PetCharacterSkin{id="skin_"+Guid.NewGuid().ToString("N"),name="新服饰",model_id=selectedModel};
        draft.skins=draft.skins.Concat(new[]{skin}).ToArray();selectedSkinId=skin.id;ShowSkin();
        status.text="已添加皮肤草稿。选择对应模型、填写名称后保存角色卡。";
    }
    private void DeleteSkin()
    {
        if(draft==null||service.IsBusy)return;
        if(draft.skins.Length<=1){status.text="角色至少需要保留一套皮肤。";return;}
        draft.skins=draft.skins.Where(skin=>skin.id!=selectedSkinId).ToArray();
        if(draft.active_skin_id==selectedSkinId)draft.active_skin_id=draft.skins[0].id;
        selectedSkinId=draft.active_skin_id;ShowSkin();status.text="皮肤已从草稿移除，保存角色卡后生效。";
    }
    private void SelectActiveSkin()
    {
        if(draft==null||service.IsBusy||!StoreSkinEdit())return;
        draft.active_skin_id=selectedSkinId;draft.model_id=selectedModel;RenderSkins();
        status.text="已设为此卡当前服饰，保存角色卡后生效。";
    }
    private void WearSkin()
    {
        if(draft==null||service.IsBusy||!StoreSkinEdit())return;
        if(newCard||draft.id!=service.ActiveId){status.text="请先保存并切换到这张角色卡，再立即穿戴。";return;}
        var saved=service.Cards.FirstOrDefault(card=>card.id==draft.id)?.skins?.FirstOrDefault(skin=>skin.id==selectedSkinId);
        if(saved==null||saved.model_id!=selectedModel){status.text="此皮肤尚未保存，或模型绑定已修改。请先保存角色卡。";return;}
        service.ActivateSkin(selectedSkinId);
    }
    private void ToggleAiSkins()
    {
        if(draft==null||service.IsBusy)return;
        draft.allow_ai_skin_switch=!draft.allow_ai_skin_switch;RenderSkins();status.text="自动换装偏好将在保存角色卡后生效。";
    }
    private bool ReadDraft()
    {
        if(draft==null||service.IsBusy)return false;
        if(string.IsNullOrWhiteSpace(nameInput.text)||string.IsNullOrWhiteSpace(personaInput.text)){status.text="请填写人物名称和人设。";return false;}
        if(!StoreSkinEdit())return false;
        draft.name=nameInput.text.Trim();draft.persona=personaInput.text.Trim();PetCharacterCards.EnsureSkins(draft);
        foreach(var pair in voice)typeof(PetCharacterCard).GetField(pair.Key).SetValue(draft,pair.Value.text.Trim());
        if(!newCard&&draft.id==service.ActiveId)draft.actions=PetActionLibrary.Export();
        return true;
    }
    private void Save() { if(!ReadDraft())return;var saved=JsonUtility.FromJson<PetCharacterCard>(JsonUtility.ToJson(draft));service.Save(saved,newCard); }
    private void SaveCopy() { if(!ReadDraft())return;var saved=JsonUtility.FromJson<PetCharacterCard>(JsonUtility.ToJson(draft));saved.id=null;saved.name+="（副本）";service.Save(saved,true); }
}
