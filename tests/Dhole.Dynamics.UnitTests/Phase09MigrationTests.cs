using System.Text.Json;
using Dhole.Dynamics.Api;
using Dhole.Dynamics.Contracts;
using Xunit;

namespace Dhole.Dynamics.UnitTests;

/// <summary>Parity over the original 88-route audited inventory, without real credentials.</summary>
public sealed class Phase09MigrationTests
{
    private static readonly LegacyBundle Bundle = LegacyMigrationService.Bundle;

    [Fact]
    public void All88RoutesHaveIndividualTraceabilityAndNoEmbeddedSecrets()
    {
        Assert.Equal("dhole.legacy.phase09.v1", Bundle.Format);
        Assert.Equal("e0490c3260372e547f8672974633104268f89233", Bundle.Source.Ref);
        Assert.Equal(88, Bundle.Entries.Count);
        Assert.Equal(87, Bundle.Entries.Count(e => e.Method == "GET"));
        Assert.Single(Bundle.Entries, e => e.Method == "PATCH");
        Assert.Equal(88, Bundle.Entries.Select(e => e.Route).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(88, Bundle.Entries.Select(e => e.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(78, Bundle.Entries.Count(e => e.Query is not null));
        Assert.All(Bundle.Entries,e=>Assert.NotEqual(e.SourceEntitySet,e.EntityName));
        Assert.Equal(10, Bundle.Entries.Count(e => e.Status == "blocked_source"));
        foreach (var (entry,index) in Bundle.Entries.Select((e,i) => (e,i)))
        {
            Assert.Equal("LEG-" + (index+1).ToString("D3"), entry.Id);
            Assert.StartsWith("/dynamics/", entry.Route);
            Assert.True(entry.SourceLine > 0);
            Assert.False(string.IsNullOrWhiteSpace(entry.EntityName));
        }
        var payload = JsonSerializer.Serialize(Bundle);
        Assert.DoesNotContain("clientSecrets",payload,StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("accessToken",payload,StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://sibaja07.crm.dynamics.com",payload,StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OriginalPatchNeedsOptimisticConcurrencyAndNoBlindStarEtag()
    {
        var patch = Assert.Single(Bundle.Entries.Where(e => e.Method == "PATCH"));
        Assert.Equal("LEG-074",patch.Id);
        Assert.Equal("/dynamics/UpdateDynamics",patch.Route);
        Assert.Equal("update",patch.Template!.Operation);
        Assert.Equal("new_bcf",Assert.Single(patch.Template.Mappings).Target);
        Assert.Null(patch.Query);
        Assert.Contains(patch.Reasons,x=>x.Contains("etag",StringComparison.Ordinal));
        Assert.Throws<ReadRejected>(()=>GenericWriteService.RequireEtag("*"));
    }

    [Fact]
    public void ProvenIncompatibilitiesAreNotSilentlyReinterpreted()
    {
        foreach(var id in new[]{"LEG-014","LEG-020","LEG-028","LEG-036","LEG-080","LEG-081","LEG-084"})
        {
            var entry=Assert.Single(Bundle.Entries, e=>e.Id==id);
            Assert.Null(entry.Query);
            Assert.NotEmpty(entry.Reasons);
            Assert.Equal("blocked_source",entry.Status);
        }
        Assert.Contains(Bundle.Entries, e=>e.Id=="LEG-036" &&
            e.Reasons.Any(r=>r.Contains("GUID",StringComparison.Ordinal)));
        Assert.DoesNotContain(Bundle.Entries,e=>e.Status=="blocked_source"&&e.Query is not null);
    }

    [Fact]
    public void CandidateFiltersBindSafelyAndCompileWithinPhase05Limits()
    {
        foreach(var e in Bundle.Entries.Where(e=>e.Query is not null))
        {
            var spec=e.Query!;
            var types = new Dictionary<string,string>(StringComparer.Ordinal);
            var fields=new HashSet<string>(spec.Select,StringComparer.Ordinal);
            foreach(var order in spec.Order??[])fields.Add(order.Field);
            Collect(spec.Filter,types,fields,e.ParameterSchema);
            var columns=fields.Select(f=>new MetadataColumn(f,f.StartsWith('_')?"Uniqueidentifier":types.GetValueOrDefault(f)??"String",
                true,!f.StartsWith('_'),!f.StartsWith('_'),f.StartsWith('_'),
                null,null,null,[],new Dictionary<int,string>(),[])).ToArray();
            var table=new MetadataTable(e.EntityName,e.EntityName,"incidentid",null,true,
                new Dictionary<int,string>(),columns,[]);
            var allowed=fields.ToHashSet(StringComparer.Ordinal);
            var bound=ReadSavedQueryBinder.Bind(spec,e.ParameterSchema,
                new Dictionary<string,JsonElement>(),validateOnly:true);
            var compiled=ReadQueryCompiler.Compile(table,bound,allowed);
            Assert.StartsWith("/api/data/v9.2/",compiled.RelativeUrl);
            Assert.Contains("$select=",compiled.RelativeUrl);
            Assert.All(e.ParameterSchema,p=>Assert.DoesNotContain("{"+p.Name+"}",compiled.RelativeUrl));
            Assert.True(compiled.RelativeUrl.Length<7000);
            Assert.NotNull(e.Template);
            Assert.Equal("find",e.Template!.Operation);
            Assert.Equal(e.QueryName,e.Template.SavedQuery);
            Assert.Equal(e.ParameterSchema.Count,e.Template.Inputs.Count);
        }
    }

    [Fact]
    public void ApostropheParametersRemainAliasesNotExecutableFilterSyntax()
    {
        var entry=Bundle.Entries.Single(e=>e.Id=="LEG-001");
        var table=new MetadataTable("incidents","incidents","incidentid",null,true,
            new Dictionary<int,string>(),entry.Query!.Select
                .Append("title").Distinct(StringComparer.Ordinal)
                .Select(name=>new MetadataColumn(name,"String",true,true,true,false,
                    null,null,null,[],new Dictionary<int,string>(),[])).ToArray(),[]);
        var payload=new Dictionary<string,JsonElement>
            {["name"]=JsonSerializer.SerializeToElement("O'Connor and false")};
        var filter=ReadSavedQueryBinder.Bind(entry.Query,entry.ParameterSchema,payload);
        var result=ReadQueryCompiler.Compile(table,filter,
            table.Columns.Select(x=>x.LogicalName).ToHashSet(StringComparer.Ordinal));
        Assert.Contains("$filter=",result.RelativeUrl);
        Assert.Contains("%27%27",result.RelativeUrl);
        Assert.DoesNotContain("{name}",result.RelativeUrl);
    }

    private static void Collect(ReadPredicate? node,Dictionary<string,string> types,
        HashSet<string> fields,IReadOnlyList<ReadParamDefinition> parameters)
    {
        if(node is null)return;
        if(node.Children is { Count: > 0 })
        {
            foreach(var child in node.Children)Collect(child,types,fields,parameters);
            return;
        }
        if(node.Field is null)return;
        fields.Add(node.Field);
        if(node.Op=="contains")types[node.Field]="String";
        else if(node.Parameter is string name)
        {
            var type=parameters.Single(p=>p.Name==name).Type;
            types[node.Field]=type switch
            {
                "guid" => "Uniqueidentifier",
                "integer" => "Integer",
                "boolean" => "Boolean",
                "decimal" => "Decimal",
                "datetime" => "DateTime",
                _ => "String"
            };
        }
        else if(node.Value is JsonElement value)
        {
            if(value.ValueKind==JsonValueKind.Array &&
                value.EnumerateArray().FirstOrDefault().ValueKind==JsonValueKind.Number)
                types[node.Field]="Picklist";
            else if(value.ValueKind==JsonValueKind.Number)types[node.Field]="Integer";
            else if(value.ValueKind is JsonValueKind.True or JsonValueKind.False)types[node.Field]="Boolean";
        }
    }

    [Fact]
    public void LookupShadowsCannotBeInjectedWithoutDiscoveredAndApprovedColumn()
    {
        var lookup = new MetadataTable("incident","incidents","incidentid",null,false,
            new Dictionary<int,string>(),[
                new MetadataColumn("customerid","Customer",true,false,false,false,
                    null,null,null,[],new Dictionary<int,string>(),[])
            ],[]);
        var query = new ReadSpec(["_customerid_value"]);
        Assert.Throws<UnauthorizedAccessException>(()=>ReadQueryCompiler.Compile(
            lookup,query,new HashSet<string>(["_customerid_value"],StringComparer.Ordinal)));
        var allowed = lookup with { Columns = [
            ..lookup.Columns,
            new MetadataColumn("_customerid_value","Uniqueidentifier",true,false,false,true,
                null,null,null,[],new Dictionary<int,string>(),[])
        ]};
        var compiled = ReadQueryCompiler.Compile(allowed,query,
            new HashSet<string>(["_customerid_value"],StringComparer.Ordinal));
        Assert.Contains("_customerid_value",compiled.RelativeUrl);
        Assert.Throws<UnauthorizedAccessException>(()=>ReadQueryCompiler.Compile(
            allowed,query,new HashSet<string>(StringComparer.Ordinal)));
    }

}
