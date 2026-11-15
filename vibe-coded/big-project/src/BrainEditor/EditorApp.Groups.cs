using System.Text.Json;
using MYT.Kit.Gui;

namespace ArcadeLab.Editor;

public sealed partial class EditorApp
{
    private bool groupsTab,linkWiringTab,hubDistributionTab;
    private int moduleInspectionRevision=-1;
    private readonly Dictionary<string,ModuleGraph> inspectedModules=[];
    private ModuleGraph InspectModule(BrainModule module)
    {
        if(moduleInspectionRevision!=topologyRevision){inspectedModules.Clear();moduleInspectionRevision=topologyRevision;}
        if(inspectedModules.TryGetValue(module.Id,out var graph))return graph;
        if(linkSceneRevision==topologyRevision&&linkScene?.ById.TryGetValue(module.Id,out var scene)==true)
        {
            graph=new();graph.Neurons.AddRange(scene.Neurons.Select(i=>linkScene.Graph.Neurons[i]));
        }
        else if(ReferenceEquals(previewModule,module)&&!previewDirty&&preview is not null)graph=preview;
        else graph=Generation.Generate(module);
        inspectedModules[module.Id]=graph;return graph;
    }
    private string neuronGroupId="output";
    private bool DrawModuleTabs(float x,float y)
    {
        bool hub=Module?.Kind==ModuleKind.Hub;
        if(Button("PROPERTIES",x,y,hub?110:125,!groupsTab&&(!hub||!hubDistributionTab))){groupsTab=false;hubDistributionTab=false;fieldPage=0;}
        if(Button("NEURON GROUPS",x+(hub?118:133),y,hub?130:160,groupsTab)){groupsTab=true;hubDistributionTab=false;fieldPage=0;}
        if(hub&&Button("DISTRIBUTION",x+256,y,146,hubDistributionTab)){groupsTab=false;hubDistributionTab=true;fieldPage=0;}
        return groupsTab;
    }
    private void DrawGroups(BrainModule module,float x,float y)
    {
        var groups=module.Groups;
        var selected=groups.FirstOrDefault(g=>g.Id==neuronGroupId)??groups.FirstOrDefault();
        if(selected is not null)neuronGroupId=selected.Id;
        Choose(new(x,y,246,30),selected?.Name??"NO GROUPS",groups.Select(g=>g.Name).ToArray(),i=>{neuronGroupId=groups[i].Id;fieldPage=0;});
        if(Button("+",x+254,y,60))
        {
            Checkpoint();string name="group";int i=0;while(groups.Any(g=>g.Name==name+i))i++;
            var group=new NeuronGroup {Name=name+i,Criterion=module.Kind==ModuleKind.Layered?GroupCriterion.Layer:GroupCriterion.IndexRange,Count=10};
            groups.Add(group);neuronGroupId=group.Id;fieldPage=0;selected=group;
        }
        if(Button("DELETE",x+322,y,80)&&selected is not null)
        {
            bool used=document.Links.Any(l=>(l.Source.ModuleId==module.Id&&l.Source.GroupIds.Contains(selected.Id))||(l.Destination.ModuleId==module.Id&&l.Destination.GroupIds.Contains(selected.Id)));
            if(used){status="GROUP IS USED BY A LINK - CHANGE ITS ENDPOINT GROUPS FIRST";return;}
            Checkpoint();groups.Remove(selected);return;
        }
        if(selected is null)return;
        var generated=InspectModule(module);var members=NeuronGroups.Resolve(module,generated.Neurons)[selected.Id];
        Label($"{members.Length} MATCHES | START 0-BASED | COUNT 0 = REST",x,y+40,1);
        Label("LAYER -1 = LAST | STEP 1 = EVERY MATCH",x,y+57,1);
        var fields=new List<string>{"Name","Kind","Criterion"};
        switch(selected.Criterion)
        {
            case GroupCriterion.Role:fields.Add("Role");break;
            case GroupCriterion.Layer:fields.Add("Layer");break;
            case GroupCriterion.SpatialBox:fields.AddRange(["Min","Max"]);break;
            case GroupCriterion.ExplicitIds:fields.Add("NeuronIds");break;
        }
        fields.AddRange(["Start","Count","Step"]);
        Inspector(selected,x,y+82,obj=>
        {
            var copy=JsonSerializer.Deserialize<BrainModule>(JsonSerializer.Serialize(module,BrainDocument.Json),BrainDocument.Json)!;
            copy.Groups[groups.IndexOf(selected)]=(NeuronGroup)obj;ReplaceModule(copy);
        },typeof(NeuronGroup),fields.ToArray());
        highlightedGroup=members.ToHashSet();highlightedGroupModule=module.Id;
    }
    private HashSet<int>? highlightedGroup;
    private string highlightedGroupModule="";
    private string EndpointDescription(LinkEndpoint endpoint)
    {
        var module=document.Modules.FirstOrDefault(m=>m.Id==endpoint.ModuleId);
        if(module is null)return "MISSING MODULE";
        if(endpoint.Scope==EndpointScope.AllNeurons)return "all";
        if(endpoint.GroupIds.Count==0)return endpoint.Scope==EndpointScope.Groups?"no groups":endpoint.Roles.ToString().ToLowerInvariant();
        return string.Join(" + ",endpoint.GroupIds.Select(id=>module.Groups.FirstOrDefault(g=>g.Id==id)?.Name??"MISSING"));
    }
    private void DrawEndpointGroups(LinkEndpoint endpoint,float x,float y,float listHeight,string title)
    {
        Label(title,x,y,1);
        var module=document.Modules.FirstOrDefault(m=>m.Id==endpoint.ModuleId);
        Choose(new(x,y+16,402,30),module?.Name??"MISSING MODULE",document.Modules.Select(m=>m.Name).ToArray(),i=>
        {
            Checkpoint();endpoint.ModuleId=document.Modules[i].Id;endpoint.GroupIds=[];endpoint.Scope=EndpointScope.AllNeurons;
        });
        if(module is null)return;
        var graph=InspectModule(module);var groups=NeuronGroups.Resolve(module,graph.Neurons);
        string[] selected=endpoint.GroupIds.Count==0&&endpoint.Scope==EndpointScope.LegacyRoles
            ?module.Groups.Where(g=>g.Criterion==GroupCriterion.Role&&(g.Role&endpoint.Roles)!=0).Select(g=>g.Id).ToArray():endpoint.GroupIds.ToArray();
        bool all=endpoint.Scope==EndpointScope.AllNeurons||(endpoint.Scope==EndpointScope.LegacyRoles&&endpoint.GroupIds.Count==0&&endpoint.Roles==NeuronRole.All);
        var list=gui.BeginList(title,new GuiRect(x,y+52,402,listHeight),module.Groups.Count+1,26);
        for(int i=list.First;i<list.End;i++)
        {
            var group=i==0?null:module.Groups[i-1];
            bool member=group is null?all:!all&&selected.Contains(group.Id);
            var row=list.Row(i);
            var action=gui.ListRow(list,i,member);
            string text=(member?"[x] ":"[ ] ")+(group is null?"All neurons":group.Name)+"  ("+(group is null?graph.Neurons.Count:groups[group.Id].Length)+")";
            gui.Label(row,text,gui.Style.Text);
            if(action!=GuiClick.None&&choiceMenu is null&&!uiCaptured)
            {
                Checkpoint();
                if(group is null){endpoint.Scope=all?EndpointScope.Groups:EndpointScope.AllNeurons;endpoint.GroupIds=[];}
                else
                {
                    endpoint.Scope=EndpointScope.Groups;endpoint.GroupIds=all?[]:selected.ToList();
                    if(member)endpoint.GroupIds.Remove(group.Id);else endpoint.GroupIds.Add(group.Id);
                }
            }
        }
        gui.EndList();
        Label($"{NeuronGroups.Select(endpoint,module,graph.Neurons,groups).Length} NEURONS SELECTED",x,y+listHeight+60,1);
    }
    private void DrawLinkEditor(BrainLink link)
    {
        float x=width-436;
        Label("EDIT LINK",24,118,2);
        if(Button("< LINK VIEW",24,148,140)){view=0;return;}
        Label("GOLD: SOURCE / CYAN: DESTINATION",178,158,1);
        DrawLinkScene();
        Rect(width-450,104,438,height-174,.04f,.08f,.11f);
        Inline("link-name",new(x,114,402,30),link.Name,value=>{Checkpoint();link.Name=value;});
        if(Button("GROUPS",x,152,125,!linkWiringTab)){linkWiringTab=false;fieldPage=0;}
        if(Button("WIRING",x+133,152,125,linkWiringTab)){linkWiringTab=true;fieldPage=0;}
        if(linkWiringTab)
        {
            Label("ONE-TO-ONE: ORDERED, EQUAL-SIZE GROUPS",x,194,1);
            Inspector(link,x,220,obj=>{Checkpoint();document.Links[document.Links.IndexOf(link)]=(BrainLink)obj;},typeof(BrainLink),
                ["Pattern","FanOut","CrossPercent","WeightKind","Weight","WeightScale","Plasticity","Seed","Source","Destination"]);
        }
        else
        {
            float listHeight=Math.Max(52,(height-484)/2);
            DrawEndpointGroups(link.Source,x,194,listHeight,"SOURCE MODULE / GROUPS");
            DrawEndpointGroups(link.Destination,x,194+listHeight+96,listHeight,"DESTINATION MODULE / GROUPS");
            Label("CLICK GROUPS TO TOGGLE MEMBERSHIP",x,height-117,1);
            Label("CREATE GROUPS IN EDIT MODULE > NEURON GROUPS",x,height-98,1);
        }
        if(linkScene?.Graph.LinkErrors.TryGetValue(link.Id,out var error)==true)status=error;
    }
}
