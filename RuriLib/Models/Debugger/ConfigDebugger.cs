﻿using IronPython.Compiler;
using IronPython.Hosting;
using IronPython.Runtime;
using PuppeteerSharp;
using RuriLib.Exceptions;
using RuriLib.Helpers.Blocks;
using RuriLib.Helpers.CSharp;
using RuriLib.Helpers.Transpilers;
using RuriLib.Legacy.LS;
using RuriLib.Legacy.Models;
using RuriLib.Logging;
using RuriLib.Models.Bots;
using RuriLib.Models.Configs;
using RuriLib.Models.Data;
using RuriLib.Models.Data.Resources;
using RuriLib.Models.Data.Resources.Options;
using RuriLib.Models.Proxies;
using RuriLib.Models.Variables;
using RuriLib.Providers.RandomNumbers;
using RuriLib.Providers.UserAgents;
using RuriLib.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Dynamic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace RuriLib.Models.Debugger
{
    public class ConfigDebugger
    {
        public IRandomUAProvider RandomUAProvider { get; set; }
        public IRNGProvider RNGProvider { get; set; }
        public RuriLibSettingsService RuriLibSettings { get; set; }
        public PluginRepository PluginRepo { get; set; }

        public bool IsRunning { get; private set; }

        public event EventHandler Started;
        public event EventHandler<BotLoggerEntry> NewLogEntry;
        public event EventHandler Stopped;

        private readonly Config config;
        private readonly DebuggerOptions options;
        private readonly BotLogger logger;
        private CancellationTokenSource cts;
        private Browser lastPuppeteerBrowser;
        private OpenQA.Selenium.WebDriver lastSeleniumBrowser;

        public ConfigDebugger(Config config, DebuggerOptions options = null, BotLogger logger = null)
        {
            this.config = config;
            this.options = options ?? new DebuggerOptions();
            this.logger = logger ?? new BotLogger();
            logger.NewEntry += OnNewEntry;
        }

        public async Task Run()
        {
            // Build the C# script if in Stack or LoliCode mode
            if (config.Mode == ConfigMode.Stack || config.Mode == ConfigMode.LoliCode)
            {
                config.CSharpScript = config.Mode == ConfigMode.Stack
                    ? Stack2CSharpTranspiler.Transpile(config.Stack, config.Settings)
                    : Loli2CSharpTranspiler.Transpile(config.LoliCodeScript, config.Settings);
            }

            if (options.UseProxy && !options.TestProxy.Contains(':'))
            {
                throw new InvalidProxyException(options.TestProxy);
            }

            if (!options.PersistLog)
            {
                logger.Clear();
            }

            // Close any previously opened browsers
            if (lastPuppeteerBrowser != null)
            {
                await lastPuppeteerBrowser.CloseAsync();
            }

            if (lastSeleniumBrowser != null)
            {
                lastSeleniumBrowser.Quit();
            }

            options.Variables.Clear();
            IsRunning = true;
            cts = new CancellationTokenSource();
            var sw = new Stopwatch();

            var wordlistType = RuriLibSettings.Environment.WordlistTypes.First(w => w.Name == options.WordlistType);
            var dataLine = new DataLine(options.TestData, wordlistType);
            var proxy = options.UseProxy ? Proxy.Parse(options.TestProxy, options.ProxyType) : null;

            var providers = new Bots.Providers(RuriLibSettings)
            {
                RNG = RNGProvider
            };

            if (!RuriLibSettings.RuriLibSettings.GeneralSettings.UseCustomUserAgentsList)
            {
                providers.RandomUA = RandomUAProvider;
            }

            // Build the BotData
            var data = new BotData(providers, config.Settings, logger, dataLine, proxy, options.UseProxy)
            {
                CancellationToken = cts.Token
            };
            using var httpClient = new HttpClient();
            data.SetObject("httpClient", httpClient);
            var runtime = Python.CreateRuntime();
            var pyengine = runtime.GetEngine("py");
            var pco = (PythonCompilerOptions)pyengine.GetCompilerOptions();
            pco.Module &= ~ModuleOptions.Optimized;
            data.SetObject("ironPyEngine", pyengine);
            data.AsyncLocker = new();

            dynamic globals = new ExpandoObject();

            var script = new ScriptBuilder()
                .Build(config.CSharpScript, config.Settings.ScriptSettings, PluginRepo);

            logger.Log($"Sliced {dataLine.Data} into:");
            foreach (var slice in dataLine.GetVariables())
            {
                var sliceValue = data.ConfigSettings.DataSettings.UrlEncodeDataAfterSlicing
                    ? Uri.EscapeDataString(slice.AsString())
                    : slice.AsString();

                logger.Log($"{slice.Name}: {sliceValue}");
            }

            // Initialize resources
            Dictionary<string, ConfigResource> resources = new();

            // Resources will need to be disposed of
            foreach (var opt in config.Settings.DataSettings.Resources)
            {
                try
                {
                    resources[opt.Name] = opt switch
                    {
                        LinesFromFileResourceOptions x => new LinesFromFileResource(x),
                        RandomLinesFromFileResourceOptions x => new RandomLinesFromFileResource(x),
                        _ => throw new NotImplementedException()
                    };
                }
                catch
                {
                    logger.Log($"Could not create resource {opt.Name}", LogColors.Tomato);
                }
            }

            // Add resources to global variables
            globals.Resources = resources;
            var scriptGlobals = new ScriptGlobals(data, globals);

            // Set custom inputs
            foreach (var input in config.Settings.InputSettings.CustomInputs)
            {
                (scriptGlobals.input as IDictionary<string, object>).Add(input.VariableName, input.DefaultAnswer);
            }

            // [LEGACY] Set up the VariablesList
            if (config.Mode == ConfigMode.Legacy)
            {
                var slices = new List<Variable>();

                foreach (var slice in dataLine.GetVariables())
                {
                    var sliceValue = data.ConfigSettings.DataSettings.UrlEncodeDataAfterSlicing
                        ? Uri.EscapeDataString(slice.AsString())
                        : slice.AsString();

                    slices.Add(new StringVariable(sliceValue) { Name = slice.Name });
                }

                var legacyVariables = new VariablesList(slices);

                foreach (var input in config.Settings.InputSettings.CustomInputs)
                {
                    legacyVariables.Set(new StringVariable(input.DefaultAnswer) { Name = input.VariableName });
                }

                data.SetObject("legacyVariables", legacyVariables);
            }

            try
            {
                sw.Start();
                Started?.Invoke(this, EventArgs.Empty);

                if (config.Mode != ConfigMode.Legacy)
                {
                    var state = await script.RunAsync(scriptGlobals, null, cts.Token);

                    foreach (var scriptVar in state.Variables)
                    {
                        try
                        {
                            var type = DescriptorsRepository.ToVariableType(scriptVar.Type);

                            if (type.HasValue && !scriptVar.Name.StartsWith("tmp_"))
                            {
                                var variable = DescriptorsRepository.ToVariable(scriptVar.Name, scriptVar.Type, scriptVar.Value);
                                variable.MarkedForCapture = data.MarkedForCapture.Contains(scriptVar.Name);
                                options.Variables.Add(variable);
                            }
                        }
                        catch
                        {
                            // The type is not supported, e.g. it was generated using custom C# code and not blocks
                            // so we just disregard it
                        }
                    }
                }
                else
                {
                    // [LEGACY] Run the LoliScript in the old way
                    var loliScript = new LoliScript(config.LoliScript);
                    var lsGlobals = new LSGlobals(data);

                    do
                    {
                        if (cts.IsCancellationRequested)
                        {
                            break;
                        }

                        await loliScript.TakeStep(lsGlobals);

                        options.Variables.Clear();
                        var legacyVariables = data.TryGetObject<VariablesList>("legacyVariables");
                        options.Variables.AddRange(legacyVariables.Variables);
                        options.Variables.AddRange(lsGlobals.Globals.Variables);
                    }
                    while (loliScript.CanProceed);
                }
            }
            catch (OperationCanceledException)
            {
                data.STATUS = "ERROR";
                logger.Log($"Operation canceled", LogColors.Tomato);
            }
            catch (Exception ex)
            {
                data.STATUS = "ERROR";

                var logErrorMessage = RuriLibSettings.RuriLibSettings.GeneralSettings.VerboseMode
                    ? ex.ToString()
                    : ex.Message;

                logger.Log($"[{data.ExecutionInfo}] {ex.GetType().Name}: {logErrorMessage}", LogColors.Tomato);
                IsRunning = false;
                throw;
            }
            finally
            {
                sw.Stop();

                logger.Log($"BOT ENDED AFTER {sw.ElapsedMilliseconds} ms WITH STATUS: {data.STATUS}");

                // Save the browsers for later use
                lastPuppeteerBrowser = data.TryGetObject<Browser>("puppeteer");
                lastSeleniumBrowser = data.TryGetObject<OpenQA.Selenium.WebDriver>("selenium");

                // Dispose stuff in data.Objects
                data.DisposeObjectsExcept(new[] { "puppeteer", "puppeteerPage", "puppeteerFrame" });

                // Dispose resources
                foreach (var resource in resources.Where(r => r.Value is IDisposable)
                    .Select(r => r.Value).Cast<IDisposable>())
                {
                    resource.Dispose();
                }

                data.AsyncLocker.Dispose();
            }

            IsRunning = false;
            Stopped?.Invoke(this, EventArgs.Empty);
        }

        public void Stop() => cts.Cancel();

        private void OnNewEntry(object sender, BotLoggerEntry entry) => NewLogEntry?.Invoke(this, entry);
    }
}
