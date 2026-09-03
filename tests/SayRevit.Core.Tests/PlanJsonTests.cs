using SayRevit.Claude;
using SayRevit.Core.Model;
using Xunit;

namespace SayRevit.Core.Tests
{
    public class PlanJsonTests
    {
        [Fact]
        public void SchemaIsValidJsonObject()
        {
            var dict = PlanJson.SchemaAsDictionary();
            Assert.True(dict.ContainsKey("properties"));
            Assert.Equal("object", dict["type"].GetString());
        }

        [Fact]
        public void ConvertsModelOutputToPlan()
        {
            const string json = @"{
              ""runs"": [{
                ""kind"": ""pipe"",
                ""size"": { ""shape"": ""round"", ""diameter_mm"": 200, ""width_mm"": 0, ""height_mm"": 0, ""is_dn"": true },
                ""length_mm"": 10000, ""direction"": ""plus_x"", ""type_name"": ""Acciaio zincato"", ""type_hints"": [""acciaio""],
                ""system_class"": ""DomesticColdWater"", ""system_name"": ""Acqua fredda"", ""level"": ""Livello 1"", ""elevation_mm"": 2700,
                ""continues_previous"": false,
                ""branches"": [{
                  ""size"": { ""shape"": ""round"", ""diameter_mm"": 15, ""width_mm"": 0, ""height_mm"": 0, ""is_dn"": true },
                  ""count"": 3, ""length_mm"": 500, ""direction"": ""up"", ""spacing_mm"": null, ""positions_mm"": []
                }]
              }],
              ""warnings"": [""numero di stacchi assunto""],
              ""unsupported"": null
            }";
            var r = PlanJson.FromJson(json, "x");
            Assert.True(r.Success);
            var run = Assert.Single(r.Plan.Runs);
            Assert.Equal(MepKind.Pipe, run.Kind);
            Assert.Equal(200, run.Size.DiameterMm);
            Assert.True(run.Size.IsNominalDn);
            Assert.Equal(DirectionKind.PlusX, run.Direction);
            Assert.Equal("Acciaio zincato", run.ExplicitTypeName);
            Assert.Equal("Livello 1", run.LevelHint);
            Assert.Equal(2700, run.ElevationMm);
            var b = Assert.Single(run.Branches);
            Assert.Equal(3, b.Count);
            Assert.Equal(DirectionKind.Up, b.Direction);
            Assert.Null(b.SpacingMm);
            Assert.Single(r.Warnings);
        }

        [Fact]
        public void UnsupportedRequestFails()
        {
            var r = PlanJson.FromJson(@"{ ""runs"": [], ""warnings"": [], ""unsupported"": ""Le pareti non sono supportate."" }", "x");
            Assert.False(r.Success);
            Assert.Contains("pareti", r.Error);
        }

        [Fact]
        public void MissingBranchSizeFallsBackToRunSize()
        {
            const string json = @"{ ""runs"": [{ ""kind"": ""duct"",
                ""size"": { ""shape"": ""rectangular"", ""diameter_mm"": 0, ""width_mm"": 400, ""height_mm"": 200, ""is_dn"": false },
                ""length_mm"": 0, ""direction"": ""default"", ""type_name"": null, ""type_hints"": [], ""system_class"": null, ""system_name"": null,
                ""level"": null, ""elevation_mm"": null, ""continues_previous"": false,
                ""branches"": [{ ""size"": { ""shape"": ""round"", ""diameter_mm"": 0, ""width_mm"": 0, ""height_mm"": 0, ""is_dn"": false },
                  ""count"": 0, ""length_mm"": 0, ""direction"": ""left"", ""spacing_mm"": 0, ""positions_mm"": [1000, 2000] }] }],
                ""warnings"": [], ""unsupported"": null }";
            var r = PlanJson.FromJson(json, "x");
            Assert.True(r.Success);
            var run = r.Plan.Runs[0];
            Assert.Equal(MepKind.Duct, run.Kind);
            Assert.Equal(3000, run.LengthMm);
            var b = run.Branches[0];
            Assert.Equal(SizeShape.Rectangular, b.Size.Shape);
            Assert.Equal(1, b.Count);
            Assert.Equal(500, b.LengthMm);
            Assert.Null(b.SpacingMm);
            Assert.Equal(2, b.PositionsMm.Count);
        }
    }
}
