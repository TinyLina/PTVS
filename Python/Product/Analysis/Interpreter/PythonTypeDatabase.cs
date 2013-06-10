﻿/* ****************************************************************************
 *
 * Copyright (c) Microsoft Corporation. 
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A 
 * copy of the license can be found in the License.html file at the root of this distribution. If 
 * you cannot locate the Apache License, Version 2.0, please send an email to 
 * vspython@microsoft.com. By using this source code in any fashion, you are agreeing to be bound 
 * by the terms of the Apache License, Version 2.0.
 *
 * You must not remove this notice, or any other, from this software.
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.PythonTools.Interpreter.Default;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Microsoft.PythonTools.Interpreter {
    /// <summary>
    /// Provides access to an on-disk store of cached intellisense information.
    /// </summary>
    public sealed class PythonTypeDatabase : ITypeDatabaseReader {
        private readonly SharedDatabaseState _sharedState;
        internal readonly Dictionary<string, IPythonModule> _modules;
        internal readonly Dictionary<IPythonType, CPythonConstant> _constants;

        /// <summary>
        /// Gets the version of the analysis format that this class reads.
        /// </summary>
        public static readonly int CurrentVersion = 21;

        private static string _completionDatabasePath;
        private static string _baselineDatabasePath;

        public PythonTypeDatabase(string databaseDirectory, Version languageVersion, IBuiltinPythonModule builtinsModule = null) {
            _sharedState = new SharedDatabaseState(databaseDirectory, languageVersion, builtinsModule);
            _sharedState.ListenForCorruptDatabase(this);
        }

        internal PythonTypeDatabase(SharedDatabaseState cloning) {
            _sharedState = cloning;
            _sharedState.ListenForCorruptDatabase(this);
            _modules = new Dictionary<string, IPythonModule>();
            _constants = new Dictionary<IPythonType, CPythonConstant>();
        }

        /// <summary>
        /// Fired when the database is discovered to be corrrupted.  This can happen because a file
        /// wasn't successfully flushed to disk, or if the user modified the database by hand.
        /// </summary>
        public event EventHandler DatabaseCorrupt;

        /// <summary>
        /// Creates a light weight copy of this PythonTypeDatabase which supports adding 
        /// </summary>
        /// <returns></returns>
        public PythonTypeDatabase Clone() {
            if (_modules != null) {
                throw new InvalidOperationException("Cannot clone an already cloned type database");
            }
            return new PythonTypeDatabase(_sharedState);
        }

        /// <summary>
        /// Asynchrousnly loads the specified extension module into the type database making the completions available.
        /// 
        /// If the module has not already been analyzed it will be analyzed and then loaded.
        /// 
        /// If the specified module was already loaded it replaces the existing module.
        /// 
        /// Returns a new Task which can be blocked upon until the analysis of the new extension module is available.
        /// 
        /// If the extension module cannot be analyzed an exception is reproted.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token which can be used to cancel the async loading of the module</param>
        /// <param name="extensionModuleFilename">The filename of the extension module to be loaded</param>
        /// <param name="interpreter">The Python interprefer which will be used to analyze the extension module.</param>
        /// <param name="moduleName">The module name of the extension module.</param>
        public Task LoadExtensionModuleAsync(IPythonInterpreterFactory interpreter, string moduleName, string extensionModuleFilename, CancellationToken cancellationToken = default(CancellationToken)) {
            if (_modules == null) {
                return MakeExceptionTask(new InvalidOperationException("Can only LoadModules into a cloned PythonTypeDatabase"));
            }

            return Task.Factory.StartNew(
                new ExtensionModuleLoader(
                    TaskScheduler.FromCurrentSynchronizationContext(),
                    this,
                    interpreter,
                    moduleName,
                    extensionModuleFilename,
                    cancellationToken
                ).LoadExtensionModule
            );
        }

        public bool UnloadExtensionModule(string moduleName) {
            return _modules.Remove(moduleName);
        }

        private static Task MakeExceptionTask(Exception e) {
            var res = new TaskCompletionSource<Task>();
            res.SetException(e);
            return res.Task;
        }

        internal class ProcessWaitHandle : WaitHandle {
            public ProcessWaitHandle(Process process) {
                Debug.Assert(process != null);
                SafeWaitHandle = new SafeWaitHandle(process.Handle, false); // Process owns the handle
            }
        }

        class ExtensionModuleLoader {
            private readonly PythonTypeDatabase _typeDb;
            private readonly IPythonInterpreterFactory _factory;
            private readonly string _moduleName;
            private readonly string _extensionFilename;
            private readonly CancellationToken _cancel;
            private readonly TaskScheduler _startScheduler;

            const string _extensionModuleInfoFile = "extensions.$list";

            public ExtensionModuleLoader(TaskScheduler startScheduler, PythonTypeDatabase typeDb, IPythonInterpreterFactory factory, string moduleName, string extensionFilename, CancellationToken cancel) {
                _typeDb = typeDb;
                _factory = factory;
                _moduleName = moduleName;
                _extensionFilename = extensionFilename;
                _cancel = cancel;
                _startScheduler = startScheduler;
            }

            public void LoadExtensionModule() {
                List<string> existingModules = new List<string>();
                string dbFile = null;
                // open the file locking it - only one person can look at the "database" of per-project analysis.
                using (var fs = OpenProjectExtensionList()) {
                    dbFile = FindDbFile(_factory, _extensionFilename, existingModules, dbFile, fs);

                    if (dbFile == null) {
                        dbFile = GenerateDbFile(_factory, _moduleName, _extensionFilename, existingModules, dbFile, fs);
                    }
                }

                // we need to access _typeDb._modules on the UI thread.
                Task.Factory.StartNew(PublishModule, dbFile, default(CancellationToken), TaskCreationOptions.None, _startScheduler).Wait();
            }

            private void PublishModule(object state) {
                _typeDb._modules[_moduleName] = new CPythonModule(_typeDb, _moduleName, (string)state, false);
            }

            private FileStream OpenProjectExtensionList() {
                for (int i = 0; i < 50 && !_cancel.IsCancellationRequested; i++) {
                    try {
                        return new FileStream(Path.Combine(_typeDb.DatabaseDirectory, _extensionModuleInfoFile), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    } catch (IOException) {
                        if (_cancel.CanBeCanceled) {
                            _cancel.WaitHandle.WaitOne(100);
                        } else {
                            System.Threading.Thread.Sleep(100);
                        }
                    }
                }

                throw new CannotAnalyzeExtensionException("Cannot access per-project extension registry.");
            }

            private string GenerateDbFile(IPythonInterpreterFactory interpreter, string moduleName, string extensionModuleFilename, List<string> existingModules, string dbFile, FileStream fs) {
                // we need to generate the DB file
                dbFile = Path.Combine(_typeDb._sharedState.DatabaseDirectory, moduleName + ".$project.idb");
                int retryCount = 0;
                while (File.Exists(dbFile)) {
                    dbFile = Path.Combine(_typeDb._sharedState.DatabaseDirectory, moduleName + "." + ++retryCount + ".$project.idb");
                }

                using (var output = interpreter.Run(
                    Path.Combine(GetPythonToolsInstallPath(), "ExtensionScraper.py"),   // script to run
                    "scrape",                                                           // scrape
                    "-",                                                                // no module name
                    extensionModuleFilename,                                            // extension module path
                    dbFile.Substring(0, dbFile.Length - 4)                              // output file path (minus .idb)
                    )) {
                    if (_cancel.CanBeCanceled) {
                        if (WaitHandle.WaitAny(new[] { _cancel.WaitHandle, output.WaitHandle }) != 1) {
                            // we were cancelled
                            return null;
                        }
                    } else {
                        output.Wait();
                    }

                    if (output.ExitCode == 0) {
                        // [FileName]|interpGuid|interpVersion|DateTimeStamp|[db_file.idb]
                        // save the new entry in the DB file
                        existingModules.Add(
                            String.Format("{0}|{1}|{2}|{3}|{4}",
                                extensionModuleFilename,
                                interpreter.Id,
                                interpreter.Configuration.Version,
                                new FileInfo(extensionModuleFilename).LastWriteTime.ToString("O"),
                                dbFile
                            )
                        );

                        fs.Seek(0, SeekOrigin.Begin);
                        fs.SetLength(0);
                        using (var sw = new StreamWriter(fs)) {
                            sw.Write(String.Join(Environment.NewLine, existingModules));
                            sw.Flush();
                        }
                    } else {
                        throw new CannotAnalyzeExtensionException(string.Join(Environment.NewLine, output.StandardErrorLines));
                    }
                }

                return dbFile;
            }

            const int extensionModuleFilenameIndex = 0;
            const int interpreterGuidIndex = 1;
            const int interpreterVersionIndex = 2;
            const int extensionTimeStamp = 3;
            const int dbFileIndex = 4;

            /// <summary>
            /// Finds the appropriate entry in our database file and returns the name of the .idb file to be loaded or null
            /// if we do not have a generated .idb file.
            /// </summary>
            private static string FindDbFile(IPythonInterpreterFactory interpreter, string extensionModuleFilename, List<string> existingModules, string dbFile, FileStream fs) {
                var reader = new StreamReader(fs);

                string line;
                while ((line = reader.ReadLine()) != null) {
                    // [FileName]|interpGuid|interpVersion|DateTimeStamp|[db_file.idb]
                    string[] columns = line.Split('|');
                    Guid interpGuid;
                    Version interpVersion;

                    if (columns.Length != 5) {
                        // malformed data...
                        continue;
                    }

                    if (File.Exists(columns[dbFileIndex])) {
                        // db file still exists
                        DateTime lastModified;
                        if (!File.Exists(columns[extensionModuleFilenameIndex]) ||  // extension has been deleted
                            !DateTime.TryParseExact(columns[extensionTimeStamp], "O", null, System.Globalization.DateTimeStyles.RoundtripKind, out lastModified) ||
                            lastModified != new FileInfo(extensionModuleFilename).LastWriteTime) { // extension has been modified

                            // cleanup the stale DB files as we go...
                            try {
                                File.Delete(columns[4]);
                            } catch (IOException) {
                            }
                            continue;
                        }
                    } else {
                        continue;
                    }

                    // check if this is the file we're looking for...
                    if (!Guid.TryParse(columns[interpreterGuidIndex], out interpGuid) ||            // corrupt data
                        interpGuid != interpreter.Id ||                         // not our interpreter
                        !Version.TryParse(columns[interpreterVersionIndex], out interpVersion) ||     // corrupt data
                        interpVersion != interpreter.Configuration.Version ||
                        String.Compare(columns[extensionModuleFilenameIndex], extensionModuleFilename, StringComparison.OrdinalIgnoreCase) != 0) {   // not our interpreter

                        // nope, but remember the line for when we re-write out the DB.
                        existingModules.Add(line);
                        continue;
                    }

                    // this is our file, but continue reading the other lines for when we write out the DB...
                    dbFile = columns[dbFileIndex];
                }
                return dbFile;
            }
        }

        /// <summary>
        /// Determines if this PythonTypeDatabase can load modules or not.  Only PythonTypeDatabases
        /// which have been cloned can load modules.  All type databases which are cloned off of a parent
        /// support adding modules and will share the common information of the parent type database.
        /// 
        /// Once a type database has been cloned it cannot be cloned again.
        /// </summary>
        public bool CanLoadModules {
            get {
                return _modules != null;
            }
        }

        public static PythonTypeDatabase CreateDefaultTypeDatabase(Version pythonLanguageVersion) {
            return new PythonTypeDatabase(BaselineDatabasePath, pythonLanguageVersion);
        }

        public IEnumerable<string> GetModuleNames() {
            foreach (var key in _sharedState.Modules.Keys) {
                yield return key;
            }

            if (_modules != null) {
                foreach (var key in _modules.Keys) {
                    yield return key;
                }
            }
        }

        public IPythonModule GetModule(string name) {
            IPythonModule res;
            if (_sharedState.Modules.TryGetValue(name, out res)) {
                return res;
            }

            if (_modules != null && _modules.TryGetValue(name, out res)) {
                return res;
            }

            return null;
        }

        public string DatabaseDirectory {
            get {
                return _sharedState.DatabaseDirectory;
            }
        }

        public IBuiltinPythonModule BuiltinModule {
            get {
                return _sharedState.BuiltinModule;
            }
            set {
                _sharedState.BuiltinModule = value;
            }
        }

        /// <summary>
        /// Invokes Analyzer.exe for the specified factory.
        /// </summary>
        public static void Generate(PythonTypeDatabaseCreationRequest request) {
            var fact = request.Factory;
            var outPath = request.OutputPath;

            ThreadPool.QueueUserWorkItem(x => {
                var path = Path.Combine(GetPythonToolsInstallPath(), "Microsoft.PythonTools.Analyzer.exe");

                Directory.CreateDirectory(CompletionDatabasePath);

                using (var output = ProcessOutput.RunHiddenAndCapture(path,
                    "/id", fact.Id.ToString("B"),
                    "/version", fact.Configuration.Version.ToString(),
                    "/python", fact.Configuration.InterpreterPath,
                    "/library", fact.Configuration.LibraryPath,
                    "/outdir", outPath,
                    "/basedb", BaselineDatabasePath,
                    "/log", Path.Combine(outPath, "AnalysisLog.txt"),
                    "/glog", Path.Combine(CompletionDatabasePath, "AnalysisLog.txt"))) {

                    output.Wait();

                    if (output.ExitCode.HasValue && output.ExitCode > -10 && output.ExitCode < 0) {
                        File.AppendAllLines(Path.Combine(CompletionDatabasePath, "AnalysisLog.txt"), 
                            new [] { "FAIL_STDLIB: " + output.Arguments }.Concat(output.StandardErrorLines));
                        request.OnFailedToStart();
                    }
                }
            });
        }

        private static void GetLibDirs(PythonTypeDatabaseCreationRequest request, out string libDir, out string virtualEnvPackages) {
            GetLibDirs(request.Factory, out libDir, out virtualEnvPackages);
        }

        internal static void GetLibDirs(IPythonInterpreterFactory factory, out string libDir, out string virtualEnvPackages) {
            libDir = Path.Combine(Path.GetDirectoryName(factory.Configuration.InterpreterPath), "Lib");
            virtualEnvPackages = null;
            if (!Directory.Exists(libDir)) {
                string virtualEnvLibDir = Path.Combine(Path.GetDirectoryName(factory.Configuration.InterpreterPath), "..\\Lib");
                string prefixFile = Path.Combine(virtualEnvLibDir, "orig-prefix.txt");
                if (Directory.Exists(virtualEnvLibDir) && File.Exists(prefixFile)) {
                    // virtual env is setup differently.  The EXE is in a Scripts directory with the Lib dir being at ..\Lib 
                    // relative to the EXEs dir.  There is alos an orig-prefix.txt which points at the normal full Python
                    // install.  Parse that file and include the normal Python install in the analysis.
                    try {
                        var lines = File.ReadAllLines(Path.Combine(prefixFile));
                        if (lines.Length >= 1 && lines[0].IndexOfAny(Path.GetInvalidPathChars()) == -1) {

                            string origLibDir = Path.Combine(lines[0], "Lib");
                            if (Directory.Exists(origLibDir)) {
                                // virtual env install
                                libDir = origLibDir;

                                virtualEnvPackages = Path.Combine(virtualEnvLibDir, "site-packages");
                            }
                        }
                    } catch (IOException) {
                    } catch (UnauthorizedAccessException) {
                    } catch (System.Security.SecurityException) {
                    }
                } else {
                    // try and find the lib dir based upon where site.py lives
                    using (var output = factory.Run("-c", "import site; print(site.__file__)")) {
                        output.Wait();
                        libDir = output.StandardOutputLines
                            .Where(line => !string.IsNullOrWhiteSpace(line) && line.IndexOfAny(Path.GetInvalidPathChars()) == -1)
                            .Select(line => Path.GetDirectoryName(line))
                            .FirstOrDefault(dir => Directory.Exists(dir));
                    }
                }
            }
        }

        private static bool DatabaseExists(string path) {
            string versionFile = Path.Combine(path, "database.ver");
            if (File.Exists(versionFile)) {
                try {
                    string allLines = File.ReadAllText(versionFile);
                    int version;
                    return Int32.TryParse(allLines, out version) && version == PythonTypeDatabase.CurrentVersion;
                } catch (IOException) {
                }
            }
            return false;
        }

        public static string GlobalLogFilename {
            get {
                return Path.Combine(CompletionDatabasePath, "AnalysisLog.txt");
            }
        }

        internal static string BaselineDatabasePath {
            get {
                if (_baselineDatabasePath == null) {
                    _baselineDatabasePath = Path.Combine(GetPythonToolsInstallPath(), "CompletionDB");
                }
                return _baselineDatabasePath;
            }
        }

        public static string CompletionDatabasePath {
            get {
                if (_completionDatabasePath == null) {
                    _completionDatabasePath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Python Tools",
                        "CompletionDB",
#if DEBUG
 "Debug",
#endif
 AssemblyVersionInfo.VSVersion
                    );
                }
                return _completionDatabasePath;
            }
        }

        void ITypeDatabaseReader.LookupType(object type, Action<IPythonType, bool> assign, PythonTypeDatabase instanceDb) {
            Debug.Assert(instanceDb == null);

            _sharedState.LookupType(type, assign, this);
        }

        string ITypeDatabaseReader.GetBuiltinTypeName(BuiltinTypeId id) {
            return _sharedState.GetBuiltinTypeName(id);
        }

        void ITypeDatabaseReader.RunFixups() {
            _sharedState.RunFixups();
        }

        void ITypeDatabaseReader.ReadMember(string memberName, Dictionary<string, object> memberValue, Action<string, IMember> assign, IMemberContainer container, PythonTypeDatabase instanceDb) {
            Debug.Assert(instanceDb == null);

            _sharedState.ReadMember(memberName, memberValue, assign, container, this);
        }

        void ITypeDatabaseReader.OnDatabaseCorrupt() {
            OnDatabaseCorrupt();
        }

        public void OnDatabaseCorrupt() {
            var dbCorrupt = DatabaseCorrupt;
            if (dbCorrupt != null) {
                dbCorrupt(this, EventArgs.Empty);
            }
        }

        internal CPythonConstant GetConstant(IPythonType type) {
            CPythonConstant constant;
            if (!_constants.TryGetValue(type, out constant)) {
                _constants[type] = constant = new CPythonConstant(type);
            }
            return constant;
        }

        // This is duplicated throughout different assemblies in PythonTools, so search for it if you update it.
        private static string GetPythonToolsInstallPath() {
            string path = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (File.Exists(Path.Combine(path, "Microsoft.PythonTools.dll"))) {
                return path;
            }

            // running from the GAC in remote attach scenario.  Look to the VS install dir.
            using (var configKey = OpenVisualStudioKey()) {
                var installDir = configKey.GetValue("InstallDir") as string;
                if (installDir != null) {
                    var toolsPath = Path.Combine(installDir, "Extensions\\Microsoft\\Python Tools for Visual Studio\\2.0");
                    if (File.Exists(Path.Combine(toolsPath, "Microsoft.PythonTools.dll"))) {
                        return toolsPath;
                    }
                }
            }

            Debug.Assert(false, "Unable to determine Python Tools installation path");
            return string.Empty;
        }

        private static Win32.RegistryKey OpenVisualStudioKey() {
            if (Environment.Is64BitOperatingSystem) {
                return RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32).OpenSubKey("Software\\Microsoft\\VisualStudio\\" + AssemblyVersionInfo.VSVersion);
            } else {
                return Microsoft.Win32.Registry.LocalMachine.OpenSubKey("Software\\Microsoft\\VisualStudio\\" + AssemblyVersionInfo.VSVersion);
            }
        }

        internal static bool TryGetLocation(Dictionary<string, object> table, ref int line, ref int column) {
            object value;
            if (table.TryGetValue("location", out value)) {
                object[] locationInfo = value as object[];
                if (locationInfo != null && locationInfo.Length == 2 && locationInfo[0] is int && locationInfo[1] is int) {
                    line = (int)locationInfo[0];
                    column = (int)locationInfo[1];
                    return true;
                }
            }
            return false;
        }

        internal IPythonModule GetInstancedModule(string name) {
            IPythonModule res;
            if (_modules.TryGetValue(name, out res)) {
                return res;
            }
            return null;
        }

        class OutputDataReceiver {
            public readonly StringBuilder Received = new StringBuilder();

            public void OutputDataReceived(object sender, DataReceivedEventArgs e) {
                Received.Append(e.Data);
            }
        }
    }

}