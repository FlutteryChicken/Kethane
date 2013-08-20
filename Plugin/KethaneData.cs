﻿using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Kethane
{
    internal class KethaneData : ScenarioModule
    {
        public static KethaneData Current
        {
            get
            {
                var game = HighLogic.CurrentGame;
                if (game == null) { return null; }

                if (!game.scenarios.Any(p => p.moduleName == typeof(KethaneData).Name))
                {
                    var proto = game.AddProtoScenarioModule(typeof(KethaneData), GameScenes.FLIGHT, GameScenes.TRACKSTATION);
                    if (proto.targetScenes.Contains(HighLogic.LoadedScene))
                    {
                        proto.Load(ScenarioRunner.fetch);
                    }
                }

                return game.scenarios.Select(s => s.moduleRef).OfType<KethaneData>().SingleOrDefault();
            }
        }

        public Dictionary<string, Dictionary<string, IBodyResources>> PlanetDeposits = new Dictionary<string,Dictionary<string,IBodyResources>>();
        public Dictionary<string, Dictionary<string, GeodesicGrid.Cell.Set>> Scans = new Dictionary<string,Dictionary<string,GeodesicGrid.Cell.Set>>();

        private Dictionary<string, ConfigNode> generatorNodes = new Dictionary<string, ConfigNode>();
        private Dictionary<string, IResourceGenerator> generators = new Dictionary<string, IResourceGenerator>();

        public ICellResource GetDepositUnder(string resourceName, Vessel vessel)
        {
            return GetCellDeposit(resourceName, vessel.mainBody, MapOverlay.GetCellUnder(vessel.mainBody, vessel.transform.position));
        }

        public ICellResource GetCellDeposit(string resourceName, CelestialBody body, GeodesicGrid.Cell cell)
        {
            if (resourceName == null || body == null || !PlanetDeposits.ContainsKey(resourceName) || !PlanetDeposits[resourceName].ContainsKey(body.name)) { return null; }

            return PlanetDeposits[resourceName][body.name].GetResource(cell);
        }

        public override void OnLoad(ConfigNode config)
        {
            var oldPath = KSPUtil.ApplicationRootPath + "saves/" + HighLogic.SaveFolder + "/kethane.cfg";
            var oldConfig = ConfigNode.Load(oldPath);
            if (oldConfig != null)
            {
                config = oldConfig;
                System.IO.File.Delete(oldPath);
            }

            if (!config.HasValue("Version") && (config.CountNodes > 0 || config.CountValues > 2))
            {
                try
                {
                    config = upgradeConfig(config);
                }
                catch (Exception e)
                {
                    Debug.LogError("[Kethane] Error upgrading legacy data: " + e);
                    config = new ConfigNode();
                }
            }

            var timer = System.Diagnostics.Stopwatch.StartNew();

            PlanetDeposits.Clear();
            Scans.Clear();

            var resourceNodes = config.GetNodes("Resource");

            foreach (var resource in KethaneController.ResourceDefinitions)
            {
                var resourceName = resource.Resource;
                var resourceNode = resourceNodes.SingleOrDefault(n => n.GetValue("Resource") == resourceName) ?? new ConfigNode();

                PlanetDeposits[resourceName] = new Dictionary<string, IBodyResources>();
                Scans[resourceName] = new Dictionary<string, GeodesicGrid.Cell.Set>();

                generatorNodes[resourceName] = resourceNode.GetNode("Generator") ?? resource.Generator;
                var generator = createGenerator(generatorNodes[resourceName].CreateCopy());
                if (generator == null)
                {
                    Debug.LogWarning("[Kethane] Defaulting to empty generator for " + resourceName);
                    generator = new EmptyResourceGenerator();
                }
                generators[resourceName] = generator;

                var bodyNodes = resourceNode.GetNodes("Body");

                foreach (var body in FlightGlobals.Bodies)
                {
                    var bodyNode = bodyNodes.SingleOrDefault(n => n.GetValue("Name") == body.name) ?? new ConfigNode();

                    PlanetDeposits[resourceName][body.name] = generator.Load(body, bodyNode.GetNode("GeneratorData"));
                    Scans[resourceName][body.name] = new GeodesicGrid.Cell.Set(5);

                    var scanMask = bodyNode.GetValue("ScanMask");
                    if (scanMask != null)
                    {
                        try
                        {
                            Scans[resourceName][body.name] = new GeodesicGrid.Cell.Set(5, Convert.FromBase64String(scanMask.Replace('.', '/').Replace('%', '=')));
                        }
                        catch (FormatException e)
                        {
                            Debug.LogError(String.Format("[Kethane] Failed to parse {0}/{1} scan string, resetting ({2})", body.name, resourceName, e.Message));
                        }
                    }
                }
            }

            timer.Stop();
            Debug.LogWarning(String.Format("Kethane deposits loaded ({0}ms)", timer.ElapsedMilliseconds));
        }

        public void ResetBodyData(ResourceDefinition resource, CelestialBody body)
        {
            var resourceName = resource.Resource;
            PlanetDeposits[resourceName][body.name] = generators[resourceName].Load(body, null);
        }

        public void ResetGeneratorConfig(ResourceDefinition resource)
        {
            var resourceName = resource.Resource;
            generatorNodes[resourceName] = resource.Generator;
            generators[resourceName] = createGenerator(generatorNodes[resourceName].CreateCopy());
            foreach (var body in FlightGlobals.Bodies)
            {
                ResetBodyData(resource, body);
            }
        }

        private static IResourceGenerator createGenerator(ConfigNode generatorNode)
        {
            var name = generatorNode.GetValue("name");
            if (name == null) { Debug.LogError("[Kethane] Could not find generator name"); return null; }

            var constructor = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => a.GetTypes())
                .Where(t => t.Name == name)
                .Where(t => t.GetInterfaces().Any(i => i == typeof(IResourceGenerator)))
                .Select(t => t.GetConstructor(new Type[] { typeof(ConfigNode) }))
                .FirstOrDefault(c => c != null);

            if (constructor == null) { Debug.LogError("[Kethane] Could not find appropriate constructor for " + name); return null; }

            try
            {
                return (IResourceGenerator)constructor.Invoke(new object[] { generatorNode });
            }
            catch (Exception e)
            {
                Debug.LogError("[Kethane] Could not instantiate " + name + ":\n" + e);
                return null;
            }
        }

        private static ConfigNode upgradeConfig(ConfigNode oldConfig)
        {
            var config = oldConfig.CreateCopy();

            var depositSeed = int.Parse(config.GetValue("Seed"));
            config.RemoveValue("Seed");

            foreach (var resourceNode in config.GetNodes("Resource"))
            {
                var resourceName = resourceNode.GetValue("Resource");
                foreach (var bodyNode in resourceNode.GetNodes("Body"))
                {
                    var bodySeed = 0;

                    if (resourceName == "Kethane")
                    {
                        if (int.TryParse(bodyNode.GetValue("SeedModifier"), out bodySeed))
                        {
                            bodyNode.RemoveValue("SeedModifier");
                        }
                        else
                        {
                            bodySeed = bodyNode.GetValue("Name").GetHashCode();
                        }
                    }

                    var dataNode = bodyNode.AddNode("GeneratorData");
                    dataNode.AddValue("Seed", depositSeed ^ bodySeed ^ (resourceName == "Kethane" ? 0 : resourceName.GetHashCode()));
                    foreach (var depositNode in bodyNode.GetNodes("Deposit"))
                    {
                        dataNode.AddValue("Deposit", depositNode.GetValue("Quantity"));
                    }
                    bodyNode.RemoveNodes("Deposit");
                }
            }

            return config;
        }

        public override void OnSave(ConfigNode configNode)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();

            configNode.AddValue("Version", System.Reflection.Assembly.GetExecutingAssembly().GetInformationalVersion());

            foreach (var resource in PlanetDeposits)
            {
                var resourceNode = new ConfigNode("Resource");
                resourceNode.AddValue("Resource", resource.Key);
                resourceNode.AddNode(generatorNodes[resource.Key]);

                foreach (var body in resource.Value)
                {
                    var bodyNode = new ConfigNode("Body");
                    bodyNode.AddValue("Name", body.Key);

                    if (Scans.ContainsKey(resource.Key) && Scans[resource.Key].ContainsKey(body.Key))
                    {
                        bodyNode.AddValue("ScanMask", Convert.ToBase64String(Scans[resource.Key][body.Key].ToByteArray()).Replace('/', '.').Replace('=', '%'));
                    }

                    var node = body.Value.Save() ?? new ConfigNode();
                    node.name = "GeneratorData";
                    bodyNode.AddNode(node);
                    resourceNode.AddNode(bodyNode);
                }

                configNode.AddNode(resourceNode);
            }

            timer.Stop();
            Debug.LogWarning(String.Format("Kethane deposits saved ({0}ms)", timer.ElapsedMilliseconds));
        }
    }
}
