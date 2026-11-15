using System.Text.Json;
using System.Text.Json.Nodes;
using GlmSharp;
using MYT.Kit.Gui;

namespace ArcadeLab.Editor;

public sealed partial class EditorApp
{
    private string? inlineId,inlineValue,inlineOriginal;
    private Action<string>? inlineCommit;
    private GuiRect inlineRect;
    private bool uiCaptured;
    private sealed record ChoiceMenu(GuiRect Anchor,string[] Items,Action<int> Choose);
    private ChoiceMenu? choiceMenu;

    private bool CommitInline()
    {
        if(inlineId is null)return true;
        try
        {
            if(inlineValue!=inlineOriginal)inlineCommit!(inlineValue!);
            inlineId=null;gui.ClearFocus();return true;
        }
        catch(Exception e){status=e.Message;return false;}
    }
    private void Inline(string id,GuiRect rect,string value,Action<string> commit)
    {
        bool active=inlineId==id;
        if(choiceMenu is not null)
        {
            gui.Rect(rect,gui.Style.FieldBackground);gui.Label(rect,active?inlineValue!:value,gui.Style.Text);return;
        }
        string draft=active?inlineValue!:value;
        if((click&&rect.Contains(new vec2(mouseX,mouseY)))||(!active&&gui.Focused==id))
        {
            if(!active&&CommitInline()){inlineId=id;inlineOriginal=value;inlineValue=value;inlineCommit=commit;active=true;gui.Focus(id);}
            inlineRect=rect;
        }
        gui.TextField(id,rect,ref draft,1_000_000);
        if(active){inlineValue=draft;inlineRect=rect;inlineCommit=commit;}
    }
    private void Choose(GuiRect rect,string label,string[] items,Action<int> apply)
    {
        if(Button(label+" v",rect.X,rect.Y,rect.Width))
        {
            CommitInline();choiceMenu=new(rect,items,apply);
        }
    }
    private void DrawChoices()
    {
        if(choiceMenu is not {} menu)return;
        float h=Math.Min(280,menu.Items.Length*28+8),w=Math.Max(160,menu.Anchor.Width);
        var rect=new GuiRect(Math.Clamp(menu.Anchor.X,4,Math.Max(4,width-w-4)),Math.Clamp(menu.Anchor.Bottom+2,4,Math.Max(4,height-h-8)),w,h);
        gui.Rect(rect,GuiColor.FromRgb(0x263D52));gui.Outline(rect,GuiColor.FromRgb(0x75B5CD));
        var list=gui.BeginList("choices",rect.Inset(4),menu.Items.Length,28);
        for(int i=list.First;i<list.End;i++)
        {
            var row=list.Row(i);bool hover=row.Contains(new vec2(mouseX,mouseY));
            if(gui.ListRow(list,i,false)!=GuiClick.None){int selected=i;choiceMenu=null;Attempt(()=>menu.Choose(selected));}
            gui.Label(row,menu.Items[i],hover?GuiColor.White:gui.Style.Text);
            // Graph content is composed after the software GUI. Mirror intersecting popup rows there.
            if(ViewportGeometry.Overlaps(CanvasContent,row))viewer.GraphLabel(gui,row.Intersect(rect.Inset(4)),menu.Items[i],GuiColor.White,hover);
        }
        gui.EndList();
        if(pressed.Contains(256)||(click&&!rect.Contains(new vec2(mouseX,mouseY))&&!menu.Anchor.Contains(new vec2(mouseX,mouseY))))choiceMenu=null;
    }
    private static Type PropertyType(Type root,string path)
    {
        foreach(string part in path.Split('.'))
        {
            int bracket=part.IndexOf('[');string name=bracket<0?part:part[..bracket];
            root=root.GetProperty(name)?.PropertyType??typeof(string);
            if(bracket>=0)root=root.IsArray?root.GetElementType()!:root.GenericTypeArguments.FirstOrDefault()??typeof(string);
        }
        return root;
    }
    private void PropertyInspector(object target,float x,float y,Action<object> apply,Type type,string[]? fields)
    {
        string fieldsKey=fields is null?"*":string.Join("/",fields);
        if(!ReferenceEquals(inspectorTarget,target)||inspectorRevision!=documentRevision||inspectorFields!=fieldsKey)
        {
            inspectorTarget=target;inspectorRevision=documentRevision;inspectorFields=fieldsKey;
            inspectorNode=JsonSerializer.SerializeToNode(target,type,BrainDocument.Json)!.AsObject();inspectorEntries=[];
            if(target is BrainModule {Kind:ModuleKind.Lattice} lattice)
                inspectorNode["AxisSizes"]=JsonSerializer.SerializeToNode(LatticeGeometry.Sizes(lattice),BrainDocument.Json);
            void Collect(JsonObject obj,string prefix,bool top)
            {
                var properties=top&&fields is not null?fields.Where(obj.ContainsKey).Select(k=>new KeyValuePair<string,JsonNode?>(k,obj[k])):obj.ToArray().AsEnumerable();
                foreach(var property in properties)
                {
                    if((property.Key=="Role"&&target is BrainModule {Kind:ModuleKind.Custom})||property.Key=="Id"||(target is BrainLink&&property.Key is "GroupIds" or "Roles" or "ModuleId" or "Scope"))continue;
                    string key=prefix+property.Key;var owner=obj;string name=property.Key;
                    if(property.Value is JsonObject nested){Collect(nested,key+".",false);continue;}
                    if(top&&target is BrainModule {Kind:ModuleKind.Lattice}&&name=="AxisSizes"&&property.Value is JsonArray sizes)
                    {
                        for(int i=0;i<sizes.Count;i++){int axis=i;inspectorEntries.Add(($"AxisSizes[{axis}]",sizes[axis],v=>sizes[axis]=v));}
                        continue;
                    }
                    inspectorEntries.Add((key,property.Value,v=>owner[name]=v));
                    if(property.Value is JsonArray array&&array.Any(n=>n is JsonObject))
                        for(int i=0;i<array.Count;i++)if(array[i] is JsonObject child)Collect(child,key+"["+i+"].",false);
                }
            }
            Collect(inspectorNode,"",true);
        }
        var node=inspectorNode!;
        int pageSize=Math.Max(1,(int)(height-y-140)/54),pages=Math.Max(1,(inspectorEntries.Count+pageSize-1)/pageSize);
        fieldPage=Math.Clamp(fieldPage,0,pages-1);int row=0;
        foreach(var entry in inspectorEntries.Skip(fieldPage*pageSize).Take(pageSize).ToArray())
        {
            float top=y+row++*54;string label=entry.Key;
            if(target is BrainModule {Kind:ModuleKind.Lattice} latticeModule)
            {
                if(entry.Key.StartsWith("AxisSizes[",StringComparison.Ordinal))
                {
                    int axis=int.Parse(entry.Key[10..^1]);label="SIZE "+(axis<3?"XYZ"[axis].ToString():"D"+(axis+1));
                }
                if(entry.Key=="Neighbors"&&latticeModule.Shape==LatticeShape.Flat)label="Neighbors / "+LatticeGeometry.Name(latticeModule.Dimensions,latticeModule.Neighbors);
            }
            if(target is BrainModule {Kind:ModuleKind.Hub})label=entry.Key switch
            {
                "PowerLaw.MinDegree"=>"Minimum outgoing degree",
                "PowerLaw.MaxDegree"=>$"Maximum outgoing degree (up to {Math.Max(0,HubCount((BrainModule)target)-1)})",
                "PowerLaw.Exponent"=>"Power-law exponent (higher = sparser)",
                _=>label
            };
            Label(label,x,top,1);
            var rect=new GuiRect(x,top+16,width-x-28,30);Type propertyType=PropertyType(type,entry.Key);
            void Apply(JsonNode? replacement)
            {
                var previous=entry.Value?.DeepClone();
                var owner=entry.Value?.Parent as JsonObject;
                JsonNode? oldAxes=null;bool resized=false;
                try
                {
                    // A dimension change and its required axis-count change are one edit/undo step.
                    if(entry.Key.EndsWith("Dimensions",StringComparison.Ordinal)&&owner?["AxisSizes"] is JsonArray {Count:>0} axes
                        &&replacement is JsonValue dimension&&dimension.TryGetValue<int>(out int count)&&count is >0 and <=64)
                    {
                        oldAxes=axes.DeepClone();var resizedAxes=(JsonArray)axes.DeepClone();
                        while(resizedAxes.Count<count)resizedAxes.Add(2);
                        while(resizedAxes.Count>count)resizedAxes.RemoveAt(resizedAxes.Count-1);
                        owner["AxisSizes"]=resizedAxes;resized=true;
                    }
                    entry.Set(replacement);var result=node.Deserialize(type,BrainDocument.Json)!;
                    if(result is BrainModule {Kind:ModuleKind.Hub} hub)PrepareHubEdit(hub,entry.Key=="NeuronCount",entry.Key.StartsWith("PowerLaw.",StringComparison.Ordinal));
                    apply(result);status="UPDATED "+entry.Key;
                }
                catch{entry.Set(previous);if(resized)owner!["AxisSizes"]=oldAxes;inspectorRevision=-1;throw;}
            }
            if(propertyType.IsEnum||propertyType==typeof(bool))
            {
                string[] values=propertyType==typeof(bool)?["false","true"]:Enum.GetNames(propertyType);
                if(propertyType==typeof(GroupCriterion)&&Module?.Kind!=ModuleKind.Layered)values=values.Where(v=>v!=nameof(GroupCriterion.Layer)).ToArray();
                Choose(rect,entry.Value?.ToString()??"",values,i=>Apply(propertyType==typeof(bool)?JsonValue.Create(i==1):JsonValue.Create(values[i])));
            }
            else if(entry.Value is JsonArray array)
            {
                var element=propertyType.IsArray?propertyType.GetElementType():propertyType.GenericTypeArguments.FirstOrDefault();
                bool simple=!array.Any(n=>n is JsonObject)&&element is not null&&(element.IsPrimitive||element==typeof(string));
                if(simple)
                {
                    string value=string.Join(", ",array.Select(n=>n?.ToString()));
                    Inline(entry.Key,rect with {Width=rect.Width-76},value,text=>
                    {
                        var items=text.Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries);
                        var replacement=new JsonArray();foreach(string item in items)replacement.Add(element==typeof(string)?JsonValue.Create(item):JsonNode.Parse(item));Apply(replacement);
                    });
                }
                else gui.Label(rect with {Width=rect.Width-76},$"{array.Count} items (settings below)",gui.Style.Text);
                if(Button("+",rect.Right-70,rect.Y,32))Attempt(()=>
                {
                    var copy=(JsonArray)array.DeepClone();var itemType=propertyType.IsArray?propertyType.GetElementType()!:propertyType.GenericTypeArguments[0];
                    object? value=itemType==typeof(int)?8:itemType==typeof(string)?"":Activator.CreateInstance(itemType);
                    copy.Add(JsonSerializer.SerializeToNode(value,itemType,BrainDocument.Json));Apply(copy);
                });
                if(Button("-",rect.Right-34,rect.Y,32)&&array.Count>0)Attempt(()=>{var copy=(JsonArray)array.DeepClone();copy.RemoveAt(copy.Count-1);Apply(copy);});
            }
            else
            {
                bool isString=propertyType==typeof(string);string value=entry.Value?.ToString()??"";
                if(entry.Key=="NeuronCount"&&target is BrainModule {NeuronCount:0} module)value=((long)module.Inputs+module.Hidden+module.Outputs).ToString();
                if(entry.Key=="PowerLaw.Exponent"&&target is BrainModule hub&&!HubExponentEnabled(hub))
                {
                    gui.Rect(rect,gui.Style.FieldBackground);gui.Label(rect,value+" (fixed degree)",gui.Style.TextDisabled);continue;
                }
                Inline(entry.Key,rect,value,text=>Apply(isString?JsonValue.Create(text):JsonNode.Parse(text)));
            }
        }
        float bottom=height-122;
        if(Button("< FIELDS",x,bottom,100))fieldPage=Math.Max(0,fieldPage-1);
        if(Button("FIELDS >",x+108,bottom,100))fieldPage=Math.Min(pages-1,fieldPage+1);
        Label($"{fieldPage+1}/{pages} | ENTER APPLY",x+222,bottom+10,1);
    }
}
