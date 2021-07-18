﻿using System;
using System.IO;
using System.Text;
using System.Linq;
using System.Collections.Immutable;
using System.Collections.Generic;
using System.Reflection;
using System.Diagnostics;

using Microsoft.Extensions.Logging;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

using RoslynPad.Roslyn;

using b7.Scripter.ViewModel;
using b7.Scripter.Services;
using b7.Scripter.Scripting;

namespace b7.Scripter.Engine
{
    public class ScriptEngine
    {
        private readonly ILogger _logger;

        private ScriptOptions _baseScriptOptions;

        public string Directory { get; }
        public IScriptHost Host { get; }
        public RoslynHost RoslynHost { get; private set; } = null!;

        private readonly List<Assembly> _referenceAssemblies;

        public ScriptEngine(ILogger<ScriptEngine> logger, IScriptHost host)
        {
            _logger = logger;
            _baseScriptOptions = ScriptOptions.Default;

            Directory = Path.GetFullPath("scripts");
            Host = host;

            _referenceAssemblies = new List<Assembly>()
            {
                typeof(object).Assembly, // System
                Assembly.Load("b7.Scripter.Common"),
                Assembly.Load("Xabbo.Common"),
                Assembly.Load("Xabbo.Core")
            };
        }

        public void Initialize()
        {
            _logger.LogInformation("Initializing script engine...");

            _baseScriptOptions = ScriptOptions.Default
                .WithLanguageVersion(LanguageVersion.CSharp8)
                .WithEmitDebugInformation(true)
                .WithOptimizationLevel(OptimizationLevel.Debug)
                .WithSourceResolver(new SourceFileResolver(new[] { "." }, Directory))
                // TODO : Metadata reference resolver
                .WithReferences(_referenceAssemblies)
                .WithImports(new[]
                {
                    "System",
                    "System.Text",
                    "System.Text.RegularExpressions",
                    "System.IO",
                    "System.Linq",
                    "System.Collections",
                    "System.Collections.Generic",
                    "Xabbo.Messages",
                    "Xabbo.Core",
                    "b7.Scripter.Runtime",
                    "b7.Scripter.Runtime.PacketTypes"
                });

            RoslynHost = new ScripterRoslynHost(
                _baseScriptOptions,
                additionalAssemblies: new[]
                {
                    Assembly.Load("RoslynPad.Roslyn.Windows"),
                    Assembly.Load("RoslynPad.Editor.Windows")
                },
                references: RoslynHostReferences.Empty.With(
                    references: _baseScriptOptions.MetadataReferences,
                    imports: _baseScriptOptions.Imports,
                    assemblyReferences: _referenceAssemblies,
                    typeNamespaceImports: new[] { typeof(G) }
                )
            );

            _logger.LogInformation("Script engine initialized.");
        }

        public bool Compile(ScriptViewModel script)
        {
            if (script.IsCompiling || script.IsRunning)
            {
                throw new InvalidOperationException($"The script is currently {(script.IsCompiling ? "compiling" : "running")}.");
            }

            try
            {
                script.IsFaulted = false;
                script.IsCompiling = true;
                script.Status = ScriptStatus.Compiling;
                script.UpdateStatus("Compiling...");
                
                script.StartTime = null;
                script.EndTime = null;

                Script<object> csharpScript = CSharpScript.Create(
                    script.Code,
                    options: _baseScriptOptions
                        .WithFilePath("script.csx")
                        .WithFileEncoding(Encoding.UTF8),
                    globalsType: typeof(G)
                );

                ImmutableArray<Diagnostic> diagnostics = csharpScript.Compile();
                if (diagnostics.Any(x => x.Severity == DiagnosticSeverity.Error))
                    throw new CompilationErrorException("Failed to compile the script", diagnostics);

                script.Runner = csharpScript.CreateDelegate();
                script.UpdateStatus(string.Empty);

                return true;
            }
            catch (CompilationErrorException ex)
            {
                script.Status = ScriptStatus.CompileError;
                script.ErrorText = "";
                script.UpdateStatus(ex.ToString());

                script.RaiseCompileError(ex);

                return false;
            }
            finally
            {
                script.IsCompiling = false;
                script.Status = ScriptStatus.None;
            }
        }

        private void Execute(ScriptViewModel script)
        {
            if (script.IsCompiling || script.IsRunning)
            {
                throw new InvalidOperationException($"The script is currently {(script.IsCompiling ? "compiling" : "running")}.");
            }

            if (script.Runner is null)
            {
                throw new InvalidOperationException("The script has not been compiled.");
            }

            Exception? error = null;

            try
            {
                script.IsFaulted = false;
                script.UpdateStatus("Executing...");
                script.Status = ScriptStatus.Executing;
                script.StartTime = DateTime.Now;
                script.EndTime = null;
                script.IsRunning = true;
                script.IsLogging = false;

                object? result = null;
                using (G globals = new(Host, script))
                {
                    result = script.Runner(globals, script.CancellationToken).GetAwaiter().GetResult();
                }

                script.Status = ScriptStatus.Complete;
                script.LogMessage(result?.ToString() ?? "Execution complete.");
            }
            catch (Exception ex)
            {
                error = ex;

                StringBuilder errorMessage = new();

                if (ex is OperationCanceledException operationCanceledEx)
                {
                    if (script.CancellationToken.IsCancellationRequested)
                    {
                        script.Status = ScriptStatus.Canceled;
                        errorMessage.Append("Execution canceled.");
                    }
                    else
                    {
                        script.Status = ScriptStatus.TimedOut;
                        errorMessage.Append("Operation timed out.");
                    }
                }
                else
                {
                    errorMessage.Append($"{ex.GetType().FullName ?? "Error"}: {ex.Message}");

                    script.IsFaulted = true;
                    script.Status = ScriptStatus.RuntimeError;
                }

                StackTrace stackTrace = new(ex, true);
                StackFrame[] frames = stackTrace.GetFrames();

                foreach (StackFrame frame in frames)
                {
                    if (!frame.HasSource()) continue;

                    MethodBase? methodBase = frame.GetMethod();
                    string? fileName = frame.GetFileName();

                    if (methodBase is null || fileName is null) continue;
                    if (methodBase.DeclaringType?.Namespace?.StartsWith("b7.Scripter") == true)
                        continue;

                    int lineNumber = frame.GetFileLineNumber();

                    fileName = Path.GetFileName(fileName);

                    errorMessage.Append("\r\n  ");
                    if (methodBase.DeclaringType?.Name.StartsWith("<<Init") != true)
                    {
                        errorMessage.Append($"at {methodBase} ");
                    }

                    errorMessage.Append($"in {fileName}:line {lineNumber}");
                }

                script.LogMessage(errorMessage.ToString());
                script.RaiseRuntimeError(ex);
            }
            finally
            {
                script.IsRunning = false;
                script.EndTime = DateTime.Now;
            }
        }

        public void Run(ScriptViewModel script)
        {
            if (!Host.CanExecute || script.IsCompiling || script.IsRunning) return;

            try
            {
                script.RenewCancellationToken();

                if (Compile(script))
                {
                    Execute(script);
                }
            }
            catch (Exception ex)
            {
                return;
            }
            finally
            {
                script.IsCancelling = false;
            }
        }
    }
}
