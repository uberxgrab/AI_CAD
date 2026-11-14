using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HvacAiCadAddin.Takeoff;

namespace HvacQtoCoreTests
{
    /// <summary>
    /// Prints what the code does for inputs the specification leaves open. These are
    /// observations for review, so they count neither as passes nor as failures.
    /// </summary>
    internal static class Probes
    {
        public static void Run()
        {
            foreach (string layer in new[]
            {
                "D15.9/9.5", "15.9/D9.5", "DN15.9/DN9.5", "EIA_1/2 RP", "15.9/9.5/6.4", "X15.9/9.5", "15.9/9.5X",
                "CHWS DN100 DN50", "RP D15.9-D9.5", "X (D34)", "D34.", "D.5", "D34,5", "15,9/9,5",
                "EIA_1 Pipe DN27/DN34",
            })
            {
                PipeLayerClassification result = TakeoffClassifier.ClassifyPipeLayer(layer);
                Console.WriteLine("layer `{0}` -> {1} [{2}]", layer, result.Material,
                    string.Join(", ", result.SizesMm.Select(TakeoffClassifier.FormatNumber)));
            }

            foreach (string name in new[]
            {
                "Duct 600 x 300", "Duct 600 X 300", "Ø 250", "D 250", "HEAD250", "D250A", "ADD250", "1D250",
                "Duct-600x300-L", "600x300x", "x600x300", "Duct 600.5x300.5", "Duct 600,5x300", "Box 500x400 600x300",
                "D250 600x300", "Duct 0x300", "Duct D0",
            })
            {
                DuctSize size;
                bool parsed = TakeoffClassifier.TryParseDuctSize(name, out size);
                Console.WriteLine("duct `{0}` -> {1}", name, parsed ? size.Text + " deciding " + size.DecidingSize : "no");
            }

            foreach (string properties in new[]
            {
                "40 mH2O - 10050 LPM", "40MH2O - 10050 lpm", "40mH20-10050LPM", "10,050 LPM - 40mH20", "40 m - 10050 l/min",
            })
            {
                TakeoffBlockInput block = new TakeoffBlockInput(Fixtures.Label)
                    .AddAttribute("TITLE", "CHWP-01").AddAttribute("PROPERTIES", properties);
                Console.WriteLine("pump `{0}` -> {1}", properties, Check.Show(TakeoffClassifier.DescribeEquipment(block).Text));
            }

            Equipment(new TakeoffBlockInput(Fixtures.Label).AddAttribute("TITLE", "chwp-01"));
            Equipment(new TakeoffBlockInput(Fixtures.Label).AddAttribute("title", "CHWP-01"));
            Equipment(new TakeoffBlockInput("hvac-name of equipments").AddAttribute("TITLE", "CHWP-01"));
            Equipment(new TakeoffBlockInput("odu-6 hp"));
            Equipment(new TakeoffBlockInput("ODU-6HP"));
            Equipment(new TakeoffBlockInput("ODU-6.5 HP"));
            Equipment(new TakeoffBlockInput("fcu-cas-16 kW"));
            Equipment(new TakeoffBlockInput("FCU-CAS-16kW"));
            Equipment(new TakeoffBlockInput("water cooled chiller - 420 rt"));
            Equipment(new TakeoffBlockInput("Water Cooled Chiller 420 RT"));
            Equipment(new TakeoffBlockInput("X").AddDynamicProperty("origin", "(1,2,3)"));
            Equipment(new TakeoffBlockInput("X").AddDynamicProperty("Distance3", "2000"));

            Console.WriteLine("hvac_pump symbol -> {0}", TakeoffClassifier.IsPumpSymbol(new TakeoffBlockInput("hvac_pump")));

            string path = Path.Combine(Path.GetTempPath(), "HvacQtoProbe_" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                TakeoffResult result = Fixtures.RowHeights(new[] { "Bad\u0001Name" });
                TakeoffWorkbookWriter.Write(path, result);
                Console.WriteLine("control character in a name -> written");
            }
            catch (Exception exception)
            {
                Console.WriteLine("control character in a name -> {0}: {1}", exception.GetType().Name, exception.Message);
            }
            finally
            {
                File.Delete(path);
            }

            var mixed = new List<TakeoffBlockInput>
            {
                new TakeoffBlockInput("Duct D700") { Distance3 = 1000 },
                new TakeoffBlockInput("Duct 600x300") { Distance3 = 1000 },
                new TakeoffBlockInput("Duct D250") { Distance3 = 1000 },
                new TakeoffBlockInput("Duct 800x300") { Distance3 = 1000 },
                new TakeoffBlockInput("Duct 300x600") { Distance3 = 1000 },
            };
            TakeoffResult ducts = TakeoffAggregator.Aggregate(mixed, new List<TakeoffCurveInput>(), DuctGaugeSettings.CreateDefault());
            Console.WriteLine("appendix order -> {0}", string.Join(" | ", ducts.DuctSizeRows.Select(row => row.SizeText)));

            var pumps = new List<TakeoffBlockInput>
            {
                new TakeoffBlockInput(Fixtures.Label).AddAttribute("TITLE", "CHWP-01"),
                new TakeoffBlockInput(Fixtures.Label).AddAttribute("TITLE", "CWP-01"),
            };
            TakeoffResult pumpRows = TakeoffAggregator.Aggregate(pumps, new List<TakeoffCurveInput>(), DuctGaugeSettings.CreateDefault());
            Console.WriteLine("pump order -> {0}", string.Join(" | ", pumpRows.EquipmentRows.Select(row => Check.Show(row.Description))));
        }

        private static void Equipment(TakeoffBlockInput block)
        {
            EquipmentDescription description = TakeoffClassifier.DescribeEquipment(block);
            Console.WriteLine("equipment `{0}` -> {1} [{2}]", block.Name, Check.Show(description.Text), description.Unit);
        }
    }
}
