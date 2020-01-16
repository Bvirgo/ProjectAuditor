using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Unity.ProjectAuditor.Editor.Utils;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using Assembly = System.Reflection.Assembly;
#if UNITY_2018_1_OR_NEWER
using UnityEditor.Build.Player;
#endif

namespace Unity.ProjectAuditor.Editor
{
    public class ScriptAuditor : IAuditor
    {
        private List<ProblemDescriptor> m_ProblemDescriptors;
        private string[] m_AssemblyNames;
        private DefaultAssemblyResolver m_AssemblyResolver;

        private readonly List<IInstructionAnalyzer> m_InstructionAnalyzers = new List<IInstructionAnalyzer>();
        private readonly List<OpCode> m_OpCodes = new List<OpCode>();
        private readonly UnityEditor.Compilation.Assembly[] m_PlayerAssemblies;
                
        public string[] assemblyNames
        {
            get
            {
                if (m_AssemblyNames != null)
                    return m_AssemblyNames;

                var names = new List<string>();
                foreach (var assembly in m_PlayerAssemblies)
                {
                    names.Add(assembly.name);                    
                }

                m_AssemblyNames = names.ToArray();
                return m_AssemblyNames;
            }
        }   
        
        public ScriptAuditor()
        {
#if UNITY_2018_1_OR_NEWER
            m_PlayerAssemblies = CompilationPipeline.GetAssemblies(AssembliesType.Player);
#else
            m_PlayerAssemblies = CompilationPipeline.GetAssemblies().Where(a => a.flags != AssemblyFlags.EditorAssembly).ToArray();
#endif
        }

        public IEnumerable<ProblemDescriptor> GetDescriptors()
        {
            return m_ProblemDescriptors;
        }

        public void Audit( ProjectReport projectReport, IProgressBar progressBar = null)
        {
            var userAssemblies = GetPlayerAssemblies();
            if (userAssemblies.Count > 0)
            {
                m_AssemblyResolver = new DefaultAssemblyResolver();
#if UNITY_2019_1_OR_NEWER
                var assemblyPaths = new List<string>();
                assemblyPaths.AddRange(CompilationPipeline.GetPrecompiledAssemblyPaths(CompilationPipeline.PrecompiledAssemblySources
                    .UserAssembly));
                assemblyPaths.AddRange(CompilationPipeline.GetPrecompiledAssemblyPaths(CompilationPipeline.PrecompiledAssemblySources
                    .UnityEngine));
                foreach (var dir in assemblyPaths.Select(path => Path.GetDirectoryName(path)).Distinct())
                {
                    m_AssemblyResolver.AddSearchDirectory(dir);    
                }
#else
                m_AssemblyResolver.AddSearchDirectory(Path.Combine(EditorApplication.applicationContentsPath, "Managed", "UnityEngine"));
#endif
                m_AssemblyResolver.AddSearchDirectory(Path.Combine(EditorApplication.applicationContentsPath, "UnityExtensions", "Unity", "GUISystem"));

                foreach (var dir in userAssemblies.Select(path => Path.GetDirectoryName(path)).Distinct())
                {
                    m_AssemblyResolver.AddSearchDirectory(dir);    
                }

                var callCrawler = new CallCrawler();                
                
                if (progressBar != null)
                    progressBar.Initialize("Analyzing Scripts", "Analyzing project scripts", m_PlayerAssemblies.Length);

                // Analyse all Player assemblies, including Package assemblies.
                foreach (var assemblyPath in userAssemblies)
                {
                    if (progressBar != null)
                        progressBar.AdvanceProgressBar(string.Format("Analyzing {0} scripts", Path.GetFileName(assemblyPath)));

                    if (!File.Exists(assemblyPath))
                    {
                        Debug.LogError(assemblyPath + " not found.");
                        continue;
                    }
                    
                    AnalyzeAssembly(assemblyPath, projectReport, callCrawler);
                }
                
                if (progressBar != null)
                    progressBar.ClearProgressBar();

                callCrawler.BuildCallHierarchies(projectReport, progressBar);
            }            
        }
        
        private List<string> GetPlayerAssemblies()
        {
            var assemblies = new List<string>();  
#if UNITY_2018_2_OR_NEWER
            var outputFolder = FileUtil.GetUniqueTempPathInProject();
            if (Directory.Exists(outputFolder))
                Directory.Delete(outputFolder, true);

            var input = new ScriptCompilationSettings();
            input.target = EditorUserBuildSettings.activeBuildTarget;
            input.@group = EditorUserBuildSettings.selectedBuildTargetGroup;

            var compilationResult = PlayerBuildInterface.CompilePlayerScripts(input, outputFolder);
            foreach (var assembly in compilationResult.assemblies)
            {
                assemblies.Add(Path.Combine(outputFolder, assembly));    
            }
#else
            // fallback to CompilationPipeline assemblies 
            foreach (var playerAssembly in m_PlayerAssemblies)
            {
                assemblies.Add(playerAssembly.outputPath);                   
            }   
#endif
            return assemblies;
        }
        
        private void AnalyzeAssembly(string assemblyPath, ProjectReport projectReport, CallCrawler callCrawler)
        {
            using (var a = AssemblyDefinition.ReadAssembly(assemblyPath, new ReaderParameters() {ReadSymbols = true, AssemblyResolver = m_AssemblyResolver}))
            {
                foreach (var methodDefinition in MonoCecilHelper.AggregateAllTypeDefinitions(a.MainModule.Types).SelectMany(t => t.Methods))
                {
                    if (!methodDefinition.HasBody)
                        continue;
                    
                    AnalyzeMethodBody(projectReport, a, methodDefinition, callCrawler);
                }     
            }
        }

        private void AnalyzeMethodBody(ProjectReport projectReport, AssemblyDefinition a, MethodDefinition caller, CallCrawler callCrawler)
        {
            if (!caller.DebugInformation.HasSequencePoints)
                return;
            
             var callerNode = new CallTreeNode(caller);

            foreach (var inst in caller.Body.Instructions.Where(i => m_OpCodes.Contains(i.OpCode)))
            {
                //var msg = string.Empty;
                SequencePoint s = null;
                for (var i = inst; i != null; i = i.Previous)
                {
                    s = caller.DebugInformation.GetSequencePoint(i);
                    if (s != null)
                    {
                        // msg = i == inst ? " exactly" : "nearby";
                        break;
                    }
                }

                var location = callerNode.location = new Location
                    {path = s.Document.Url.Replace("\\", "/"), line = s.StartLine};
                    
                if (inst.OpCode == OpCodes.Call || inst.OpCode == OpCodes.Callvirt)
                {
                    callCrawler.Add(caller, (MethodReference) inst.Operand, location);
                }

                foreach (var analyzer in m_InstructionAnalyzers)
                {
                    if (analyzer.GetOpCodes().Contains(inst.OpCode))
                    {
                        var projectIssue = analyzer.Analyze(caller, inst);
                        if (projectIssue != null)
                        {
                            projectIssue.callTree.AddChild(callerNode);
                            projectIssue.location = location;
                            projectIssue.assembly = a.Name.Name;

                            projectReport.AddIssue(projectIssue);
                        }                        
                    }
                }               
            }
        }

        public void LoadDatabase(string path)
        {
            m_ProblemDescriptors = ProblemDescriptorHelper.LoadProblemDescriptors(path, "ApiDatabase");
            
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            foreach (var assembly in assemblies)
            {
                var problemDescriptorTypes = GetAnalyzerTypes(assembly);

                foreach (var type in problemDescriptorTypes)
                {
                    AddAnalyzer(Activator.CreateInstance(type, this) as IInstructionAnalyzer);
                }
            }
        }

        public IEnumerable<Type> GetAnalyzerTypes(Assembly assembly)
        {
            foreach(var type in assembly.GetTypes()) {
                if (type.GetCustomAttributes(typeof(ScriptAnalyzerAttribute), true).Length > 0) {
                    yield return type;
                }
            }
        }

        void AddAnalyzer(IInstructionAnalyzer analyzer)
        {
            m_InstructionAnalyzers.Add(analyzer);
            m_OpCodes.AddRange(analyzer.GetOpCodes());
        }
        
        public void RegisterDescriptor(ProblemDescriptor descriptor)
        {
            // TODO: check for id conflict
            m_ProblemDescriptors.Add(descriptor);
        }
        
        public static IEnumerable<ProjectIssue> FindScriptIssues(ProjectReport projectReport, string relativePath)
        {
            return projectReport.GetIssues(IssueCategory.ApiCalls).Where(i => i.relativePath.Equals(relativePath));
        }
    }
}
