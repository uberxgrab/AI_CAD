using System.Collections.Generic;
using System.Linq;
using HvacAiCadAddin.Takeoff;

namespace HvacQtoCoreTests
{
    /// <summary>Specification section 4: equipment descriptions, grouping and pump rule.</summary>
    internal static class EquipmentTests
    {
        private const string Label = "HVAC-NAME OF EQUIPMENTS";
        private const string ChilledPump = "Bơm tuần hoàn nước lạnh:";
        private const string CondenserPump = "Bơm tuần hoàn nước giải nhiệt:";
        private const string FlowLine = "\n- Lưu lượng nước: 10050 (LPM)";
        private const string HeadLine = "\n- Cột áp: 40 (mH2O)";

        public static void Run()
        {
            MappedTexts();
            PumpLines();
            NotMappedOnPurpose();
            FallbackParameters();
            GroupingAndCounts();
            SortOrder();
            PumpDoubleCount();
        }

        private static void Describe(string name, TakeoffBlockInput block, string text, string unit)
        {
            Check.Case(name, () =>
            {
                EquipmentDescription description = TakeoffClassifier.DescribeEquipment(block);
                Check.Equal(name + " text", text, description.Text);
                Check.Equal(name + " unit", unit, description.Unit);
            });
        }

        private static TakeoffBlockInput Pump(string title, string properties)
        {
            TakeoffBlockInput block = new TakeoffBlockInput(Label).AddAttribute("TITLE", title);
            return properties == null ? block : block.AddAttribute("PROPERTIES", properties);
        }

        private static TakeoffResult Blocks(params TakeoffBlockInput[] blocks)
        {
            return TakeoffAggregator.Aggregate(blocks, new List<TakeoffCurveInput>(), DuctGaugeSettings.CreateDefault());
        }

        private static void MappedTexts()
        {
            Describe("s4 ODU", new TakeoffBlockInput("ODU-6 HP"), "Dàn nóng ODU 6 HP", "bộ");
            Describe("s4 ODU 10", new TakeoffBlockInput("ODU-10 HP"), "Dàn nóng ODU 10 HP", "bộ");
            Describe("s4 FCU-CAS", new TakeoffBlockInput("FCU-CAS-16 kW"), "Dàn lạnh VRV Cassette âm trần Inverter 16 kW", "bộ");
            Describe("s4 FCU-CAS decimal", new TakeoffBlockInput("FCU-CAS-2.8 kW"), "Dàn lạnh VRV Cassette âm trần Inverter 2.8 kW", "bộ");
            Describe("s4 chiller", new TakeoffBlockInput("Water Cooled Chiller - 420 RT"),
                "Chiller giải nhiệt nước:\n- Công suất lạnh: 420 RT", "bộ");
        }

        private static void PumpLines()
        {
            Describe("s4 CHWP", Pump("CHWP-01", "40mH20 - 10050 LPM"), ChilledPump + FlowLine + HeadLine, "bộ");
            Describe("s4 CWP", Pump("CWP-01", "40mH20 - 10050 LPM"), CondenserPump + FlowLine + HeadLine, "bộ");
            Describe("s4 mH2O spelling", Pump("CHWP-01", "40mH2O - 10050 LPM"), ChilledPump + FlowLine + HeadLine, "bộ");
            Describe("s4 mH20 typo", Pump("CHWP-02", "40mH20 - 10050 LPM"), ChilledPump + FlowLine + HeadLine, "bộ");
            Describe("s4 flow printed first whatever the source order", Pump("CHWP-01", "10050 LPM - 40mH2O"),
                ChilledPump + FlowLine + HeadLine, "bộ");
            Describe("s4 decimals kept", Pump("CWP-01", "12.5mH2O - 1500.5 LPM"),
                CondenserPump + "\n- Lưu lượng nước: 1500.5 (LPM)\n- Cột áp: 12.5 (mH2O)", "bộ");
            Describe("s4 comma thousands", Pump("CHWP-01", "40mH20 - 10,050 LPM"), ChilledPump + FlowLine + HeadLine, "bộ");
            Describe("s4 comma decimal", Pump("CWP-01", "12,5mH2O - 1500,5 LPM"),
                CondenserPump + "\n- Lưu lượng nước: 1500.5 (LPM)\n- Cột áp: 12.5 (mH2O)", "bộ");
            Describe("s4 head missing", Pump("CHWP-01", "10050 LPM"), ChilledPump + FlowLine, "bộ");
            Describe("s4 flow missing", Pump("CHWP-01", "40mH20"), ChilledPump + HeadLine, "bộ");
            Describe("s4 properties empty", Pump("CHWP-01", ""), ChilledPump, "bộ");
            Describe("s4 properties absent", Pump("CWP-01", null), CondenserPump, "bộ");
            Describe("s4 properties unrelated text", Pump("CHWP-01", "TBD"), ChilledPump, "bộ");

            Check.Case("s4 nothing invented on pumps", () =>
            {
                string text = TakeoffClassifier.DescribeEquipment(Pump("CHWP-01", "40mH20 - 10050 LPM")).Text;
                foreach (string word in new[] { "COP", "R134", "R410", "motor", "Motor", "kW", "HP" })
                    Check.False("s4 pump text has no `" + word + "`", text.Contains(word), Check.Show(text));
            });

            Describe("s4 other TITLE uses fallback", Pump("AHU-01", "2000 LPS"),
                Label + "\nPROPERTIES=2000 LPS; TITLE=AHU-01", "cái");
            Describe("s4 TITLE must start with the code", Pump("XCHWP-01", null), Label + "\nTITLE=XCHWP-01", "cái");
            Describe("s4 label without TITLE", new TakeoffBlockInput(Label), Label, "cái");
        }

        private static void NotMappedOnPurpose()
        {
            foreach (string name in new[]
            {
                "FCU-2.2 kW",
                "IDU-160000 BTUH",
                "AHU-01",
                "TRANE_Celing ducted FCU_120000 BTUH",
                "Sa box-500x400x500",
                "HVAC_PUMP",
            })
            {
                Describe("s4 stays fallback `" + name + "`", new TakeoffBlockInput(name), name, "cái");
            }
        }

        private static void FallbackParameters()
        {
            Describe("s4 attributes and dynamic properties",
                new TakeoffBlockInput("AHU-01").AddAttribute("TAG", "A1").AddDynamicProperty("Visibility", "On"),
                "AHU-01\nDynamic:Visibility=On; TAG=A1", "cái");

            // The old code sorts with StringComparer.OrdinalIgnoreCase.
            Describe("s4 sort ignores case",
                new TakeoffBlockInput("X1").AddAttribute("b", "1").AddAttribute("A", "2").AddAttribute("C", "3"),
                "X1\nA=2; b=1; C=3", "cái");

            Describe("s4 Origin skipped, others kept",
                new TakeoffBlockInput("AHU-01").AddDynamicProperty("Origin", "(1,2,3)").AddDynamicProperty("Distance1", "500"),
                "AHU-01\nDynamic:Distance1=500", "cái");

            Describe("s4 only Origin leaves the name alone",
                new TakeoffBlockInput("AHU-01").AddDynamicProperty("Origin", "(1,2,3)"), "AHU-01", "cái");

            Describe("s4 attribute named Origin is kept",
                new TakeoffBlockInput("AHU-01").AddAttribute("Origin", "A"), "AHU-01\nOrigin=A", "cái");

            Check.Case("s4 Origin no longer splits rows", () =>
            {
                TakeoffResult result = Blocks(
                    new TakeoffBlockInput("AHU-01").AddDynamicProperty("Origin", "(1,2,3)").AddAttribute("TAG", "A"),
                    new TakeoffBlockInput("AHU-01").AddDynamicProperty("Origin", "(9,8,7)").AddAttribute("TAG", "A"));
                Check.Equal("s4 one row", 1, result.EquipmentRows.Count);
                Check.Equal("s4 count two", 2, result.EquipmentRows[0].Count);
            });
        }

        private static void GroupingAndCounts()
        {
            Check.Case("s4 group by final description", () =>
            {
                TakeoffResult result = Blocks(
                    new TakeoffBlockInput("ODU-6 HP"),
                    new TakeoffBlockInput("ODU-6 HP"),
                    new TakeoffBlockInput("ODU-6 HP"),
                    Pump("CHWP-01", "40mH20 - 10050 LPM"),
                    Pump("CHWP-02", "40mH2O - 10050 LPM"),
                    new TakeoffBlockInput("AHU-01").AddAttribute("TAG", "A"),
                    new TakeoffBlockInput("AHU-01").AddAttribute("TAG", "B"));

                Check.Equal("s4 row count", 4, result.EquipmentRows.Count);
                EquipmentRow odu = result.EquipmentRows.First(row => row.Description == "Dàn nóng ODU 6 HP");
                Check.Equal("s4 ODU count", 3, odu.Count);
                Check.Equal("s4 ODU unit on row", "bộ", odu.Unit);
                EquipmentRow pump = result.EquipmentRows.First(row => row.Description == ChilledPump + FlowLine + HeadLine);
                Check.Equal("s4 two pump titles, one description", 2, pump.Count);
            });
        }

        private static void SortOrder()
        {
            Check.Case("s4 rule order then natural order", () =>
            {
                TakeoffResult result = Blocks(
                    new TakeoffBlockInput("AHU-10"),
                    new TakeoffBlockInput("AHU-2"),
                    Pump("CHWP-01", "40mH20 - 10050 LPM"),
                    new TakeoffBlockInput("Water Cooled Chiller - 420 RT"),
                    new TakeoffBlockInput("Water Cooled Chiller - 100 RT"),
                    new TakeoffBlockInput("FCU-CAS-16 kW"),
                    new TakeoffBlockInput("FCU-CAS-2.8 kW"),
                    new TakeoffBlockInput("ODU-10 HP"),
                    new TakeoffBlockInput("ODU-6 HP"));

                Check.Sequence("s4 equipment order",
                    new[]
                    {
                        "Dàn nóng ODU 6 HP",
                        "Dàn nóng ODU 10 HP",
                        "Dàn lạnh VRV Cassette âm trần Inverter 2.8 kW",
                        "Dàn lạnh VRV Cassette âm trần Inverter 16 kW",
                        "Chiller giải nhiệt nước:\n- Công suất lạnh: 100 RT",
                        "Chiller giải nhiệt nước:\n- Công suất lạnh: 420 RT",
                        ChilledPump + FlowLine + HeadLine,
                        "AHU-2",
                        "AHU-10",
                    },
                    result.EquipmentRows.Select(row => row.Description));
            });

            Check.Case("s4 rule rank", () =>
            {
                int odu = TakeoffClassifier.DescribeEquipment(new TakeoffBlockInput("ODU-6 HP")).SortRank;
                int cassette = TakeoffClassifier.DescribeEquipment(new TakeoffBlockInput("FCU-CAS-16 kW")).SortRank;
                int chiller = TakeoffClassifier.DescribeEquipment(new TakeoffBlockInput("Water Cooled Chiller - 420 RT")).SortRank;
                int chilledPump = TakeoffClassifier.DescribeEquipment(Pump("CHWP-01", null)).SortRank;
                int condenserPump = TakeoffClassifier.DescribeEquipment(Pump("CWP-01", null)).SortRank;
                int other = TakeoffClassifier.DescribeEquipment(new TakeoffBlockInput("AHU-01")).SortRank;
                Check.True("s4 ODU before cassette", odu < cassette);
                Check.True("s4 cassette before chiller", cassette < chiller);
                Check.True("s4 chiller before pump", chiller < chilledPump);
                Check.Equal("s4 both pumps same rule", chilledPump, condenserPump);
                Check.True("s4 pump before others", chilledPump < other);
            });
        }

        private static void PumpDoubleCount()
        {
            Check.Case("s4 recognizers", () =>
            {
                Check.True("s4 CHWP is a pump label", TakeoffClassifier.IsPumpLabel(Pump("CHWP-01", null)));
                Check.True("s4 CWP is a pump label", TakeoffClassifier.IsPumpLabel(Pump("CWP-01", null)));
                Check.False("s4 other TITLE is no pump label", TakeoffClassifier.IsPumpLabel(Pump("AHU-01", null)));
                Check.False("s4 symbol is no pump label", TakeoffClassifier.IsPumpLabel(new TakeoffBlockInput("HVAC_PUMP")));
                Check.True("s4 HVAC_PUMP is a pump symbol", TakeoffClassifier.IsPumpSymbol(new TakeoffBlockInput("HVAC_PUMP")));
                Check.False("s4 label is no pump symbol", TakeoffClassifier.IsPumpSymbol(Pump("CHWP-01", null)));
            });

            Check.Case("s4 pump label excludes symbols", () =>
            {
                TakeoffResult result = Blocks(
                    new TakeoffBlockInput("HVAC_PUMP"),
                    Pump("CWP-01", "40mH20 - 10050 LPM"),
                    new TakeoffBlockInput("HVAC_PUMP"),
                    new TakeoffBlockInput("HVAC_PUMP"));
                Check.Equal("s4 excluded count", 3, result.ExcludedPumpBlockCount);
                Check.Sequence("s4 only the label remains",
                    new[] { CondenserPump + FlowLine + HeadLine },
                    result.EquipmentRows.Select(row => row.Description));
            });

            Check.Case("s4 symbols stay without a label", () =>
            {
                TakeoffResult result = Blocks(new TakeoffBlockInput("HVAC_PUMP"), new TakeoffBlockInput("HVAC_PUMP"));
                Check.Equal("s4 nothing excluded", 0, result.ExcludedPumpBlockCount);
                Check.Equal("s4 one fallback row", 1, result.EquipmentRows.Count);
                Check.Equal("s4 fallback text", "HVAC_PUMP", result.EquipmentRows[0].Description);
                Check.Equal("s4 fallback unit", "cái", result.EquipmentRows[0].Unit);
                Check.Equal("s4 fallback count", 2, result.EquipmentRows[0].Count);
            });

            Check.Case("s4 unrecognized label does not exclude symbols", () =>
            {
                TakeoffResult result = Blocks(Pump("AHU-01", null), new TakeoffBlockInput("HVAC_PUMP"));
                Check.Equal("s4 nothing excluded", 0, result.ExcludedPumpBlockCount);
                Check.True("s4 HVAC_PUMP kept", result.EquipmentRows.Any(row => row.Description == "HVAC_PUMP"));
            });
        }
    }
}
