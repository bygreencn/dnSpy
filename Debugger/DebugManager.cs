﻿/*
    Copyright (C) 2014-2015 de4dot@gmail.com

    This file is part of dnSpy

    dnSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    dnSpy is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with dnSpy.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using dndbg.COM.CorDebug;
using dndbg.Engine;
using dnlib.DotNet;
using dnlib.PE;
using dnSpy.Contracts.App;
using dnSpy.Contracts.Files;
using dnSpy.Contracts.Files.Tabs;
using dnSpy.Contracts.Files.Tabs.TextEditor;
using dnSpy.Contracts.Files.TreeView;
using dnSpy.Contracts.Menus;
using dnSpy.Contracts.TreeView;
using dnSpy.Debugger.CallStack;
using dnSpy.Debugger.Dialogs;
using dnSpy.Debugger.IMModules;
using dnSpy.Shared.UI.Files;
using ICSharpCode.Decompiler;

namespace dnSpy.Debugger {
	interface IDebugManager {
		IDnSpyFile GetCurrentExecutableAssembly(IMenuItemContext context);
	}

	[ExportFileListListener]
	sealed class DebugManagerFileListListener : IFileListListener {
		public bool CanLoad {
			get { return !debugManager.Value.TheDebugger.IsDebugging; }
		}

		public bool CanReload {
			get { return !debugManager.Value.TheDebugger.IsDebugging; }
		}

		readonly Lazy<DebugManager> debugManager;

		[ImportingConstructor]
		DebugManagerFileListListener(Lazy<DebugManager> debugManager) {
			this.debugManager = debugManager;
		}

		public void AfterLoad(bool isReload) {
		}

		public void BeforeLoad(bool isReload) {
		}
	}

	[Export, Export(typeof(IDebugManager)), PartCreationPolicy(CreationPolicy.Shared)]
	sealed class DebugManager : IDebugManager {
		readonly IAppWindow appWindow;
		readonly IFileTabManager fileTabManager;
		readonly IMessageBoxManager messageBoxManager;
		readonly IDebuggerSettings debuggerSettings;
		readonly ITheDebugger theDebugger;
		readonly IStackFrameManager stackFrameManager;
		readonly Lazy<IModuleLoader> moduleLoader;
		readonly Lazy<IInMemoryModuleManager> inMemoryModuleManager;
		readonly ISerializedDnSpyModuleCreator serializedDnSpyModuleCreator;

		public ITheDebugger TheDebugger {
			get { return theDebugger; }
		}

		[DllImport("user32")]
		static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

		[return: MarshalAs(UnmanagedType.Bool)]
		[DllImport("user32")]
		static extern bool SetForegroundWindow(IntPtr hWnd);

		[ImportingConstructor]
		DebugManager(IAppWindow appWindow, IFileTabManager fileTabManager, IMessageBoxManager messageBoxManager, IDebuggerSettings debuggerSettings, ITheDebugger theDebugger, IStackFrameManager stackFrameManager, Lazy<IModuleLoader> moduleLoader, Lazy<IInMemoryModuleManager> inMemoryModuleManager, ISerializedDnSpyModuleCreator serializedDnSpyModuleCreator) {
			this.appWindow = appWindow;
			this.fileTabManager = fileTabManager;
			this.messageBoxManager = messageBoxManager;
			this.debuggerSettings = debuggerSettings;
			this.theDebugger = theDebugger;
			this.stackFrameManager = stackFrameManager;
			this.moduleLoader = moduleLoader;
			this.inMemoryModuleManager = inMemoryModuleManager;
			this.serializedDnSpyModuleCreator = serializedDnSpyModuleCreator;
			stackFrameManager.PropertyChanged += StackFrameManager_PropertyChanged;
			theDebugger.ProcessRunning += TheDebugger_ProcessRunning;
			theDebugger.OnProcessStateChanged += TheDebugger_OnProcessStateChanged;
			appWindow.MainWindowClosing += AppWindow_MainWindowClosing;
			debuggerSettings.PropertyChanged += DebuggerSettings_PropertyChanged;
		}

		void StackFrameManager_PropertyChanged(object sender, PropertyChangedEventArgs e) {
			if (e.PropertyName == "SelectedThread")
				UpdateCurrentLocation(stackFrameManager.FirstILFrame);
		}

		void TheDebugger_ProcessRunning(object sender, EventArgs e) {
			try {
				var e2 = (DebuggedProcessRunningEventArgs)e;
				var hWnd = e2.Process.MainWindowHandle;
				if (hWnd != IntPtr.Zero)
					SetForegroundWindow(hWnd);
			}
			catch {
			}
		}

		void DebuggerSettings_PropertyChanged(object sender, PropertyChangedEventArgs e) {
			if (e.PropertyName == "IgnoreBreakInstructions") {
				if (TheDebugger.Debugger != null)
					TheDebugger.Debugger.Options.IgnoreBreakInstructions = debuggerSettings.IgnoreBreakInstructions;
			}
			else if (e.PropertyName == "UseMemoryModules") {
				if (ProcessState != DebuggerProcessState.Terminated && debuggerSettings.UseMemoryModules)
					UpdateCurrentLocationToInMemoryModule();
			}
		}

		public DebuggerProcessState ProcessState {
			get { return TheDebugger.ProcessState; }
		}

		public bool IsDebugging {
			get { return ProcessState != DebuggerProcessState.Terminated; }
		}

		/// <summary>
		/// true if we've attached to a process
		/// </summary>
		public bool HasAttached {
			get { return IsDebugging && TheDebugger.Debugger.HasAttached; }
		}

		void SetRunningStatusMessage() {
			appWindow.StatusBar.Show("Running...");
		}

		void SetReadyStatusMessage(string msg) {
			if (string.IsNullOrEmpty(msg))
				appWindow.StatusBar.Show("Ready");
			else
				appWindow.StatusBar.Show(string.Format("Ready - {0}", msg));
		}

		void TheDebugger_OnProcessStateChanged(object sender, DebuggerEventArgs e) {
			const string DebuggingTitleInfo = "Debugging";
			var dbg = (DnDebugger)sender;
			switch (ProcessState) {
			case DebuggerProcessState.Starting:
				DebugCallbackEvent_counter = 0;
				dbg.DebugCallbackEvent += DnDebugger_DebugCallbackEvent;
				currentLocation = null;
				currentMethod = null;
				appWindow.StatusBar.Open();
				SetRunningStatusMessage();
				appWindow.AddTitleInfo(DebuggingTitleInfo);
				Application.Current.Resources["IsDebuggingKey"] = true;
				break;

			case DebuggerProcessState.Continuing:
				break;

			case DebuggerProcessState.Running:
				if (dbg.IsEvaluating)
					break;
				SetRunningStatusMessage();
				break;

			case DebuggerProcessState.Stopped:
				// If we're evaluating, or if eval has completed, don't do a thing. This code
				// should only be executed when a BP hits or if a stepping operation has completed.
				if (dbg.IsEvaluating || dbg.EvalCompleted)
					break;

				BringMainWindowToFrontAndActivate();

				UpdateCurrentLocation();
				if (currentMethod != null && currentLocation != null)
					JumpToCurrentStatement(fileTabManager.GetOrCreateActiveTab());

				SetReadyStatusMessage(new StoppedMessageCreator().GetMessage(dbg));
				break;

			case DebuggerProcessState.Terminated:
				dbg.DebugCallbackEvent -= DnDebugger_DebugCallbackEvent;
				currentLocation = null;
				currentMethod = null;
				appWindow.StatusBar.Close();
				lastDebugProcessOptions = null;
				appWindow.RemoveTitleInfo(DebuggingTitleInfo);
				Application.Current.Resources["IsDebuggingKey"] = false;
				break;
			}

			// This is sometimes needed. Press Ctrl+Shift+F5 a couple of times and the toolbar
			// debugger icons aren't updated until you release Ctrl+Shift.
			if (dbg.ProcessState == DebuggerProcessState.Stopped || !IsDebugging)
				CommandManager.InvalidateRequerySuggested();

			if (dbg.ProcessState == DebuggerProcessState.Stopped)
				ShowExceptionMessage();
		}
		CodeLocation? currentLocation = null;

		void BringMainWindowToFrontAndActivate() {
			SetWindowPos(new WindowInteropHelper(appWindow.MainWindow).Handle, IntPtr.Zero, 0, 0, 0, 0, 3);
			appWindow.MainWindow.Activate();
		}

		struct StoppedMessageCreator {
			StringBuilder sb;

			public string GetMessage(DnDebugger debugger) {
				if (debugger == null || debugger.ProcessState != DebuggerProcessState.Stopped)
					return null;

				sb = new StringBuilder();

				bool seenIlbp = false;
				foreach (var state in debugger.DebuggerStates) {
					foreach (var stopState in state.StopStates) {
						switch (stopState.Reason) {
						case DebuggerStopReason.Other:
							Append("Unknown Reason");
							break;

						case DebuggerStopReason.UnhandledException:
							Append("Unhandled Exception");
							break;

						case DebuggerStopReason.Exception:
							Append("Exception");
							break;

						case DebuggerStopReason.DebugEventBreakpoint:
							if (state.EventArgs != null)
								Append(GetEventDescription(state.EventArgs));
							else
								Append("DebugEvent");
							break;

						case DebuggerStopReason.AnyDebugEventBreakpoint:
							if (state.EventArgs != null)
								Append(GetEventDescription(state.EventArgs));
							else
								Append("Any DebugEvent");
							break;

						case DebuggerStopReason.Break:
							Append("Break Instruction");
							break;

						case DebuggerStopReason.ILCodeBreakpoint:
							if (seenIlbp)
								break;
							seenIlbp = true;
							Append("IL Breakpoint");
							break;

						case DebuggerStopReason.Step:
							break;
						}
					}
				}

				return sb.ToString();
			}

			string GetEventDescription(DebugCallbackEventArgs e) {
				CorModule mod;
				switch (e.Type) {
				case DebugCallbackType.Exception:
					var ex1Args = (ExceptionDebugCallbackEventArgs)e;
					return ex1Args.Unhandled ? "Unhandled Exception" : "Exception";

				case DebugCallbackType.CreateProcess:
					var cpArgs = (CreateProcessDebugCallbackEventArgs)e;
					var p = cpArgs.CorProcess;
					if (p == null)
						break;
					return string.Format("CreateProcess PID={0} CLR v{1}", p.ProcessId, p.CLRVersion);

				case DebugCallbackType.CreateThread:
					var ctArgs = (CreateThreadDebugCallbackEventArgs)e;
					var t = ctArgs.CorThread;
					if (t == null)
						break;
					return string.Format("CreateThread TID={0} VTID={1}", t.ThreadId, t.VolatileThreadId);

				case DebugCallbackType.LoadModule:
					var lmArgs = (LoadModuleDebugCallbackEventArgs)e;
					mod = lmArgs.CorModule;
					if (mod == null)
						break;
					if (mod.IsDynamic || mod.IsInMemory)
						return string.Format("LoadModule DYN={0} MEM={1} {2:X8} {3:X8} {4}", mod.IsDynamic ? 1 : 0, mod.IsInMemory ? 1 : 0, mod.Address, mod.Size, mod.Name);
					return string.Format("LoadModule A={0:X8} S={1:X8} {2}", mod.Address, mod.Size, mod.Name);

				case DebugCallbackType.LoadClass:
					var lcArgs = (LoadClassDebugCallbackEventArgs)e;
					var cls = lcArgs.CorClass;
					mod = cls == null ? null : cls.Module;
					if (mod == null)
						break;
					return string.Format("LoadClass 0x{0:X8} {1} {2}", cls.Token, FilterLongName(cls.ToString()), mod.Name);

				case DebugCallbackType.DebuggerError:
					var deArgs = (DebuggerErrorDebugCallbackEventArgs)e;
					return string.Format("DebuggerError hr=0x{0:X8} error=0x{1:X8}", deArgs.HError, deArgs.ErrorCode);

				case DebugCallbackType.CreateAppDomain:
					var cadArgs = (CreateAppDomainDebugCallbackEventArgs)e;
					var ad = cadArgs.CorAppDomain;
					if (ad == null)
						break;
					return string.Format("CreateAppDomain {0} {1}", ad.Id, ad.Name);

				case DebugCallbackType.LoadAssembly:
					var laArgs = (LoadAssemblyDebugCallbackEventArgs)e;
					var asm = laArgs.CorAssembly;
					if (asm == null)
						break;
					return string.Format("LoadAssembly {0}", asm.Name);

				case DebugCallbackType.ControlCTrap:
					return "Ctrl+C";

				case DebugCallbackType.BreakpointSetError:
					var bpseArgs = (BreakpointSetErrorDebugCallbackEventArgs)e;
					return string.Format("BreakpointSetError error=0x{0:X8}", bpseArgs.Error);

				case DebugCallbackType.Exception2:
					var ex2Args = (Exception2DebugCallbackEventArgs)e;
					var sb = new StringBuilder();
					sb.Append(string.Format("Exception Offset={0:X4} ", ex2Args.Offset));
					switch (ex2Args.EventType) {
					case CorDebugExceptionCallbackType.DEBUG_EXCEPTION_FIRST_CHANCE:
						sb.Append("FirstChance");
						break;
					case CorDebugExceptionCallbackType.DEBUG_EXCEPTION_USER_FIRST_CHANCE:
						sb.Append("UserFirstChance");
						break;
					case CorDebugExceptionCallbackType.DEBUG_EXCEPTION_CATCH_HANDLER_FOUND:
						sb.Append("CatchHandlerFound");
						break;
					case CorDebugExceptionCallbackType.DEBUG_EXCEPTION_UNHANDLED:
						sb.Append("Unhandled");
						break;
					default:
						sb.Append("Unknown");
						break;
					}
					return sb.ToString();

				case DebugCallbackType.MDANotification:
					var mdan = (MDANotificationDebugCallbackEventArgs)e;
					var mda = mdan.CorMDA;
					if (mda == null)
						return "MDA Notification";
					return string.Format("MDA Notification: TID={0} {1} {2}", mda.OSThreadId, mda.Name, mda.Description);
				}

				return e.Type.ToString();
			}

			void Append(string msg) {
				if (sb.Length > 0)
					sb.Append(", ");
				sb.Append(msg);
			}

			static string FilterLongName(string s) {
				const int MAX_LEN = 128;
				if (s.Length <= MAX_LEN)
					return s;
				return s.Substring(0, MAX_LEN / 2) + "..." + s.Substring(s.Length - (MAX_LEN - MAX_LEN / 2));
			}
		}

		void AppWindow_MainWindowClosing(object sender, CancelEventArgs e) {
			if (IsDebugging) {
				var result = messageBoxManager.ShowIgnorableMessage("debug: exit program", "Do you want to stop debugging?", MsgBoxButton.Yes | MsgBoxButton.No);
				if (result == MsgBoxButton.None || result == MsgBoxButton.No)
					e.Cancel = true;
			}
		}

		static string GetIncompatiblePlatformErrorMessage() {
			if (IntPtr.Size == 4)
				return "Use dnSpy.exe to debug 64-bit applications.";
			return "Use dnSpy-x86.exe to debug 32-bit applications.";
		}

		bool DebugProcess(DebugProcessOptions options) {
			if (IsDebugging)
				return false;
			if (options == null)
				return false;

			TheDebugger.RemoveDebugger();

			DnDebugger newDebugger;
			try {
				newDebugger = DnDebugger.DebugProcess(options);
			}
			catch (Exception ex) {
				var cex = ex as COMException;
				const int ERROR_NOT_SUPPORTED = unchecked((int)0x80070032);
				if (cex != null && cex.ErrorCode == ERROR_NOT_SUPPORTED)
					messageBoxManager.Show(string.Format("Could not start the debugger. {0}", GetIncompatiblePlatformErrorMessage()));
				else if (cex != null && cex.ErrorCode == CordbgErrors.CORDBG_E_UNCOMPATIBLE_PLATFORMS)
					messageBoxManager.Show(string.Format("Could not start the debugger. {0}", GetIncompatiblePlatformErrorMessage()));
				else if (cex != null && cex.ErrorCode == unchecked((int)0x800702E4))
					messageBoxManager.Show("Could not start the debugger. The debugged program requires admin privileges. Restart dnSpy with admin rights and try again.");
				else
					messageBoxManager.Show(string.Format("Could not start the debugger. Make sure you have access to the file '{0}'\n\nError: {1}", options.Filename, ex.Message));
				return false;
			}
			TheDebugger.Initialize(newDebugger);

			return true;
		}

		void ShowExceptionMessage() {
			var dbg = TheDebugger.Debugger;
			if (dbg == null)
				return;
			if (dbg.Current.GetStopState(DebuggerStopReason.Exception) == null)
				return;
			var thread = dbg.Current.Thread;
			if (thread == null)
				return;
			var exValue = thread.CorThread.CurrentException;
			if (exValue == null)
				return;
			var exType = exValue.ExactType;
			var name = exType == null ? null : exType.ToString(TypePrinterFlags.ShowNamespaces);
			var msg = string.Format("Exception thrown: '{0}' in {1}\n\nIf there is a handler for this exception, the program may be safely continued.", name, Path.GetFileName(thread.Process.Filename));
			BringMainWindowToFrontAndActivate();
			messageBoxManager.Show(msg);
		}

		void DnDebugger_DebugCallbackEvent(DnDebugger dbg, DebugCallbackEventArgs e) {
			try {
				DebugCallbackEvent_counter++;

				if (DebugCallbackEvent_counter > 1)
					return;
				if (e.Type == DebugCallbackType.Exception2) {
					var ee = (Exception2DebugCallbackEventArgs)e;
					if (ee.EventType == CorDebugExceptionCallbackType.DEBUG_EXCEPTION_UNHANDLED)
						UnhandledException(ee);
				}
				else if (e.Type == DebugCallbackType.DebuggerError)
					OnDebuggerError((DebuggerErrorDebugCallbackEventArgs)e);
			}
			finally {
				DebugCallbackEvent_counter--;
			}
		}
		int DebugCallbackEvent_counter = 0;

		void UnhandledException(Exception2DebugCallbackEventArgs e) {
			if (UnhandledException_counter != 0)
				return;
			try {
				UnhandledException_counter++;
				theDebugger.SetUnhandledException(UnhandledException_counter != 0);

				Debug.Assert(e.EventType == CorDebugExceptionCallbackType.DEBUG_EXCEPTION_UNHANDLED);
				var thread = e.CorThread;
				var exValue = thread == null ? null : thread.CurrentException;

				var sb = new StringBuilder();
				AddExceptionInfo(sb, exValue, "Exception");
				var innerExValue = EvalUtils.ReflectionReadExceptionInnerException(exValue);
				if (innerExValue != null && innerExValue.IsReference && !innerExValue.IsNull)
					AddExceptionInfo(sb, innerExValue, "\n\nInner Exception");

				var process = TheDebugger.Debugger.Processes.FirstOrDefault(p => p.Threads.Any(t => t.CorThread == thread));
				CorProcess cp;
				var processName = process != null ? Path.GetFileName(process.Filename) : string.Format("pid {0}", (cp = thread.Process) == null ? 0 : cp.ProcessId);
				BringMainWindowToFrontAndActivate();
				var res = messageBoxManager.Show(string.Format("An unhandled exception occurred in {0}\n\n{1}\n\nPress OK to stop, and Cancel to let the program run.", processName, sb), MsgBoxButton.OK | MsgBoxButton.Cancel);
				if (res != MsgBoxButton.Cancel)
					e.AddStopReason(DebuggerStopReason.UnhandledException);
			}
			finally {
				UnhandledException_counter--;
				theDebugger.SetUnhandledException(UnhandledException_counter != 0);
			}
		}
		int UnhandledException_counter = 0;

		void OnDebuggerError(DebuggerErrorDebugCallbackEventArgs e) {
			string msg;
			if (e.HError == CordbgErrors.CORDBG_E_UNCOMPATIBLE_PLATFORMS)
				msg = GetIncompatiblePlatformErrorMessage();
			else
				msg = string.Format("A CLR debugger error occurred. Terminate the debugged process and try again.\n\nHR: 0x{0:X8}\nError: 0x{1:X8}", e.HError, e.ErrorCode);
			BringMainWindowToFrontAndActivate();
			messageBoxManager.Show(msg);
		}

		static void AddExceptionInfo(StringBuilder sb, CorValue exValue, string msg) {
			var exType = exValue == null ? null : exValue.ExactType;
			int? hr = EvalUtils.ReflectionReadExceptionHResult(exValue);
			string exMsg = EvalUtils.ReflectionReadExceptionMessage(exValue);
			string exTypeString = exType == null ? "<Unknown Exception Type>" : exType.ToString();
			var s = string.Format("{0}: {1}\n\nMessage: {2}\n\nHResult: 0x{3:X8}", msg, exTypeString, exMsg, hr ?? -1);
			sb.Append(s);
		}

		public bool CanDebugCurrentAssembly(object parameter) {
			return GetCurrentExecutableAssembly(parameter as IMenuItemContext) != null;
		}

		public void DebugCurrentAssembly(object parameter) {
			var asm = GetCurrentExecutableAssembly(parameter as IMenuItemContext);
			if (asm == null)
				return;
			DebugAssembly(GetDebugAssemblyOptions(CreateDebugProcessVM(asm)));
		}

		public IDnSpyFile GetCurrentExecutableAssembly(IMenuItemContext context) {
			if (context == null)
				return null;
			if (IsDebugging)
				return null;

			IFileTreeNodeData node;
			if (context.CreatorObject.Guid == new Guid(MenuConstants.GUIDOBJ_TEXTEDITORCONTROL_GUID)) {
				var uiContext = context.FindByType<ITextEditorUIContext>();
				if (uiContext == null)
					return null;
				var nodes = uiContext.FileTab.Content.Nodes.ToArray();
				if (nodes.Length == 0)
					return null;
				node = nodes[0];
			}
			else if (context.CreatorObject.Guid == new Guid(MenuConstants.GUIDOBJ_FILES_TREEVIEW_GUID)) {
				var nodes = context.FindByType<IFileTreeNodeData[]>();
				if (nodes == null || nodes.Length == 0)
					return null;
				node = nodes[0];
			}
			else
				return null;

			return GetCurrentExecutableAssembly(node, true);
		}

		static IDnSpyFile GetCurrentExecutableAssembly(ITreeNodeData node, bool mustBeNetExe) {
			var fileNode = (node as IFileTreeNodeData).GetDnSpyFileNode();
			if (fileNode == null)
				return null;

			var file = fileNode.DnSpyFile;
			var peImage = file.PEImage;
			if (peImage == null)
				return null;
			if ((peImage.ImageNTHeaders.FileHeader.Characteristics & Characteristics.Dll) != 0)
				return null;
			if (mustBeNetExe) {
				var mod = file.ModuleDef;
				if (mod == null)
					return null;
				if (mod.Assembly == null || mod.Assembly.ManifestModule != mod)
					return null;
				if (mod.ManagedEntryPoint == null && mod.NativeEntryPoint == 0)
					return null;
			}

			return file;
		}

		IDnSpyFile GetCurrentExecutableAssembly(bool mustBeNetExe) {
			return GetCurrentExecutableAssembly(fileTabManager.FileTreeView.TreeView.SelectedItem, mustBeNetExe);
		}

		public bool CanStartWithoutDebugging {
			get { return !IsDebugging && GetCurrentExecutableAssembly(false) != null; }
		}

		public void StartWithoutDebugging() {
			var asm = GetCurrentExecutableAssembly(false);
			if (asm == null || !File.Exists(asm.Filename))
				return;
			try {
				Process.Start(asm.Filename);
			}
			catch (Exception ex) {
				messageBoxManager.Show(string.Format("Could not start '{0}'\n:ERROR: {0}", asm.Filename, ex.Message));
			}
		}

		DebugCoreCLRVM CreateDebugCoreCLRVM(IDnSpyFile asm = null) {
			// Re-use the previous one if it's the same file
			if (lastDebugCoreCLRVM != null && asm != null) {
				if (StringComparer.OrdinalIgnoreCase.Equals(lastDebugCoreCLRVM.Filename, asm.Filename))
					return lastDebugCoreCLRVM.Clone();
			}

			var vm = new DebugCoreCLRVM();
			if (asm != null)
				vm.Filename = asm.Filename;
			vm.DbgShimFilename = debuggerSettings.CoreCLRDbgShimFilename;
			vm.BreakProcessType = debuggerSettings.BreakProcessType;
			return vm;
		}

		public bool CanDebugCoreCLRAssembly {
			get { return !IsDebugging; }
		}

		public void DebugCoreCLRAssembly() {
			if (!CanDebugAssembly)
				return;
			DebugCoreCLRVM vm = null;
			if (vm == null) {
				var asm = GetCurrentExecutableAssembly(true);
				if (asm != null)
					vm = CreateDebugCoreCLRVM(asm);
			}
			if (vm == null)
				vm = lastDebugCoreCLRVM ?? CreateDebugCoreCLRVM();
			DebugAssembly(GetDebugAssemblyOptions(vm.Clone()));
		}
		DebugCoreCLRVM lastDebugCoreCLRVM;

		DebugProcessOptions GetDebugAssemblyOptions(DebugCoreCLRVM vm, bool askUser = true) {
			if (askUser) {
				var win = new DebugCoreCLRDlg();
				win.DataContext = vm;
				win.Owner = appWindow.MainWindow;
				if (win.ShowDialog() != true)
					return null;
			}

			var opts = new DebugProcessOptions(new CoreCLRTypeDebugInfo(vm.DbgShimFilename, vm.HostFilename, vm.HostCommandLine));
			opts.DebugMessageDispatcher = WpfDebugMessageDispatcher.Instance;
			opts.CurrentDirectory = vm.CurrentDirectory;
			opts.Filename = vm.Filename;
			opts.CommandLine = vm.CommandLine;
			opts.BreakProcessType = vm.BreakProcessType;
			lastDebugCoreCLRVM = vm;
			return opts;
		}

		DebugProcessVM CreateDebugProcessVM(IDnSpyFile asm = null) {
			// Re-use the previous one if it's the same file
			if (lastDebugProcessVM != null && asm != null) {
				if (StringComparer.OrdinalIgnoreCase.Equals(lastDebugProcessVM.Filename, asm.Filename))
					return lastDebugProcessVM.Clone();
			}

			var vm = new DebugProcessVM();
			if (asm != null)
				vm.Filename = asm.Filename;
			vm.BreakProcessType = debuggerSettings.BreakProcessType;
			return vm;
		}

		public bool CanDebugAssembly {
			get { return !IsDebugging; }
		}

		public void DebugAssembly() {
			if (!CanDebugAssembly)
				return;
			DebugProcessVM vm = null;
			if (vm == null) {
				var asm = GetCurrentExecutableAssembly(true);
				if (asm != null)
					vm = CreateDebugProcessVM(asm);
			}
			if (vm == null)
				vm = lastDebugProcessVM ?? CreateDebugProcessVM();
			DebugAssembly(GetDebugAssemblyOptions(vm.Clone()));
		}
		DebugProcessVM lastDebugProcessVM;

		DebugProcessOptions GetDebugAssemblyOptions(DebugProcessVM vm, bool askUser = true) {
			if (askUser) {
				var win = new DebugProcessDlg();
				win.DataContext = vm;
				win.Owner = appWindow.MainWindow;
				if (win.ShowDialog() != true)
					return null;
			}

			var opts = new DebugProcessOptions(new DesktopCLRTypeDebugInfo());
			opts.DebugMessageDispatcher = WpfDebugMessageDispatcher.Instance;
			opts.CurrentDirectory = vm.CurrentDirectory;
			opts.Filename = vm.Filename;
			opts.CommandLine = vm.CommandLine;
			opts.BreakProcessType = vm.BreakProcessType;
			lastDebugProcessVM = vm;
			return opts;
		}

		public bool CanRestart {
			get { return IsDebugging && lastDebugProcessOptions != null && !HasAttached; }
		}

		public void Restart() {
			if (!CanRestart)
				return;

			var oldOpts = lastDebugProcessOptions;
			Stop();
			TheDebugger.RemoveAndRaiseEvent();
			lastDebugProcessOptions = oldOpts;

			DebugAssembly(lastDebugProcessOptions);
		}

		void DebugAssembly(DebugProcessOptions options) {
			if (options == null)
				return;
			var optionsCopy = options.Clone();
			if (!DebugProcess(options))
				return;
			lastDebugProcessOptions = optionsCopy;
		}
		DebugProcessOptions lastDebugProcessOptions = null;

		public bool CanAttach {
			get { return !IsDebugging; }
		}

		public void Attach() {
			if (!CanAttach)
				return;

			var data = new AttachProcessVM(Dispatcher.CurrentDispatcher, debuggerSettings.SyntaxHighlightAttach);
			var win = new AttachProcessDlg();
			win.DataContext = data;
			win.Owner = appWindow.MainWindow;
			var res = win.ShowDialog();
			data.Dispose();
			if (res != true)
				return;

			var processVM = data.SelectedProcess;
			if (processVM == null)
				return;

			var options = new AttachProcessOptions(processVM.CLRTypeInfo);
			options.ProcessId = processVM.PID;
			options.DebugMessageDispatcher = WpfDebugMessageDispatcher.Instance;
			Attach(options);
		}

		bool Attach(AttachProcessOptions options) {
			if (IsDebugging)
				return false;
			if (options == null)
				return false;

			TheDebugger.RemoveDebugger();

			DnDebugger newDebugger;
			try {
				newDebugger = DnDebugger.Attach(options);
			}
			catch (Exception ex) {
				messageBoxManager.Show(string.Format("Could not start debugger.\n\nError: {0}", ex.Message));
				return false;
			}
			TheDebugger.Initialize(newDebugger);

			return true;
		}

		public bool CanBreak {
			get { return ProcessState == DebuggerProcessState.Starting || ProcessState == DebuggerProcessState.Running; }
		}

		public void Break() {
			if (!CanBreak)
				return;

			int hr = TheDebugger.Debugger.TryBreakProcesses();
			if (hr < 0)
				messageBoxManager.Show(string.Format("Could not break process. Error: 0x{0:X8}", hr));
		}

		public bool CanStop {
			get { return IsDebugging; }
		}

		public void Stop() {
			if (!CanStop)
				return;
			TheDebugger.Debugger.TerminateProcesses();
		}

		public bool CanDetach {
			get { return ProcessState != DebuggerProcessState.Continuing && ProcessState != DebuggerProcessState.Terminated; }
		}

		public void Detach() {
			if (!CanDetach)
				return;
			int hr = TheDebugger.Debugger.TryDetach();
			if (hr < 0)
				messageBoxManager.Show(string.Format("Could not detach process. Error: 0x{0:X8}", hr));
		}

		public bool CanContinue {
			get { return ProcessState == DebuggerProcessState.Stopped; }
		}

		public void Continue() {
			if (!CanContinue)
				return;
			TheDebugger.Debugger.Continue();
		}

		public void JumpToCurrentStatement(IFileTab tab) {
			JumpToCurrentStatement(tab, true);
		}

		void JumpToCurrentStatement(IFileTab tab, bool canRefreshMethods) {
			if (tab == null)
				return;
			if (currentMethod == null)
				return;

			// The file could've been added lazily to the list so add a short delay before we select it
			Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => {
				tab.FollowReference(currentMethod, false, e => {
					Debug.Assert(e.Tab == tab);
					Debug.Assert(e.Tab.UIContext is ITextEditorUIContext);
					if (e.Success)
						MoveCaretToCurrentStatement(e.Tab.UIContext as ITextEditorUIContext, canRefreshMethods);
				});
			}));
		}

		bool MoveCaretToCurrentStatement(ITextEditorUIContext uiContext, bool canRefreshMethods) {
			if (uiContext == null)
				return false;
			if (currentLocation == null)
				return false;
			if (DebugUtils.MoveCaretTo(uiContext, currentLocation.Value.SerializedDnSpyToken, currentLocation.Value.Offset))
				return true;
			if (!canRefreshMethods)
				return false;

			RefreshMethodBodies(uiContext);

			return false;
		}

		void RefreshMethodBodies(ITextEditorUIContext uiContext) {
			if (currentLocation == null)
				return;
			if (currentMethod == null)
				return;
			if (uiContext == null)
				return;

			// If this fails, we're probably in the prolog or epilog. Shouldn't normally happen.
			if (!currentLocation.Value.IsExact && !currentLocation.Value.IsApproximate)
				return;
			var body = currentMethod.Body;
			if (body == null)
				return;
			// If the offset is a valid instruction in the body, the method is probably not encrypted
			if (body.Instructions.Any(i => i.Offset == currentLocation.Value.Offset))
				return;

			// No instruction with the current offset: it must be encrypted, and since we're executing
			// the method, we must be using an invalid method body. Use a copy of the module in
			// memory, and refresh the method bodies in case it's already loaded, and re-decompile
			// the method.

			var mod = currentMethod.Module;
			if (mod == null)
				return;
			var modNode = fileTabManager.FileTreeView.FindNode(mod);
			if (modNode == null)
				return;
			var memFile = modNode.DnSpyFile as MemoryModuleDefFile;
			IDnSpyFile file = memFile;
			if (memFile == null) {
				if (modNode.DnSpyFile is CorModuleDefFile)
					return;
				var corMod = currentLocation.Value.Function.Module;
				if (corMod == null || corMod.IsDynamic)
					return;
				var dnMod = moduleLoader.Value.GetDnModule(corMod);
				file = inMemoryModuleManager.Value.LoadFile(dnMod, true);
				Debug.Assert(file != null);
				memFile = file as MemoryModuleDefFile;
			}
			if (file == null)
				return;
			// It's null if we couldn't load the file from memory because the PE / COR20 headers
			// are corrupt (eg. an obfuscator overwrote various fields with garbage). In that case,
			// file is a CorModuleDefFile and it's using the MD API to read the MD.
			if (memFile != null)
				inMemoryModuleManager.Value.UpdateModuleMemory(memFile);
			UpdateCurrentMethod(file);
			JumpToCurrentStatement(uiContext.FileTab, false);
		}

		void UpdateCurrentLocationToInMemoryModule() {
			UpdateCurrentMethod();
			if (currentMethod != null && currentLocation != null)
				JumpToCurrentStatement(fileTabManager.GetOrCreateActiveTab());
		}

		struct CodeLocation {
			public readonly CorFunction Function;
			public uint Offset;
			public CorDebugMappingResult Mapping;

			public uint Token {
				get { return Function.Token; }
			}

			public bool IsExact {
				get { return (Mapping & CorDebugMappingResult.MAPPING_EXACT) != 0; }
			}

			public bool IsApproximate {
				get { return (Mapping & CorDebugMappingResult.MAPPING_APPROXIMATE) != 0; }
			}

			public SerializedDnSpyToken SerializedDnSpyToken {
				get {
					var mod = Function.Module;
					if (mod == null)
						return new SerializedDnSpyToken();
					return new SerializedDnSpyToken(mod.SerializedDnModule.ToSerializedDnSpyModule(), Function.Token);
				}
			}

			public CodeLocation(CorFunction func, uint offset, CorDebugMappingResult mapping) {
				this.Function = func;
				this.Offset = offset;
				this.Mapping = mapping;
			}

			public static bool SameMethod(CodeLocation a, CodeLocation b) {
				return a.Function == b.Function;
			}
		}

		void UpdateCurrentLocation() {
			UpdateCurrentLocation(TheDebugger.Debugger.Current.ILFrame);
		}

		public void UpdateCurrentLocation(CorFrame frame) {
			var newLoc = GetCodeLocation(frame);

			if (currentLocation == null || newLoc == null) {
				currentLocation = newLoc;
				UpdateCurrentMethod();
				return;
			}
			if (!CodeLocation.SameMethod(currentLocation.Value, newLoc.Value)) {
				currentLocation = newLoc;
				UpdateCurrentMethod();
				return;
			}

			currentLocation = newLoc;
		}

		void UpdateCurrentMethod(IDnSpyFile file = null) {
			if (currentLocation == null) {
				currentMethod = null;
				return;
			}

			if (file == null)
				file = moduleLoader.Value.LoadModule(currentLocation.Value.Function.Module, true);
			Debug.Assert(file != null);
			var loadedMod = file == null ? null : file.ModuleDef;
			if (loadedMod == null) {
				currentMethod = null;
				return;
			}

			currentMethod = loadedMod.ResolveToken(currentLocation.Value.Token) as MethodDef;
			Debug.Assert(currentMethod != null);
		}
		MethodDef currentMethod;

		CodeLocation? GetCodeLocation(CorFrame frame) {
			if (ProcessState != DebuggerProcessState.Stopped)
				return null;
			if (frame == null)
				return null;
			var func = frame.Function;
			if (func == null)
				return null;

			return new CodeLocation(func, frame.GetILOffset(moduleLoader.Value), frame.ILFrameIP.Mapping);
		}

		StepRange[] GetStepRanges(DnDebugger debugger, CorFrame frame, bool isStepInto) {
			if (frame == null)
				return null;
			if (!frame.IsILFrame)
				return null;
			if (frame.ILFrameIP.IsUnmappedAddress)
				return null;

			var key = CreateMethodKey(debugger, frame);
			if (key == null)
				return null;

			MemberMapping mapping;
			var tab = fileTabManager.GetOrCreateActiveTab();
			var uiContext = tab.TryGetTextEditorUIContext();
			var cm = uiContext.GetCodeMappings();
			if ((mapping = cm.TryGetMapping(key.Value)) == null) {
				// User has decompiled some other code or switched to another tab
				UpdateCurrentMethod();
				JumpToCurrentStatement(tab);

				// It could be cached and immediately available. Check again
				uiContext = tab.TryGetTextEditorUIContext();
				cm = uiContext.GetCodeMappings();
				if ((mapping = cm.TryGetMapping(key.Value)) == null)
					return null;
			}

			bool isMatch;
			var scm = mapping.GetInstructionByOffset(frame.GetILOffset(moduleLoader.Value), out isMatch);
			uint[] ilRanges;
			if (scm == null)
				ilRanges = mapping.ToArray(null, false);
			else
				ilRanges = scm.ToArray(isMatch);

			if (ilRanges.Length == 0)
				return null;
			return CreateStepRanges(ilRanges);
		}

		static StepRange[] CreateStepRanges(uint[] ilRanges) {
			var stepRanges = new StepRange[ilRanges.Length / 2];
			if (stepRanges.Length == 0)
				return null;
			for (int i = 0; i < stepRanges.Length; i++)
				stepRanges[i] = new StepRange(ilRanges[i * 2], ilRanges[i * 2 + 1]);
			return stepRanges;
		}

		static SerializedDnSpyToken? CreateMethodKey(DnDebugger debugger, CorFrame frame) {
			var sma = frame.SerializedDnModule;
			if (sma == null)
				return null;

			return new SerializedDnSpyToken(sma.Value.ToSerializedDnSpyModule(), frame.Token);
		}

		CorFrame GetCurrentILFrame() {
			return stackFrameManager.FirstILFrame;
		}

		CorFrame GetCurrentMethodILFrame() {
			return stackFrameManager.SelectedFrame;
		}

		public bool CanStepInto {
			get { return ProcessState == DebuggerProcessState.Stopped && GetCurrentILFrame() != null; }
		}

		public void StepInto() {
			if (!CanStepInto)
				return;

			var ranges = GetStepRanges(TheDebugger.Debugger, GetCurrentILFrame(), true);
			TheDebugger.Debugger.StepInto(ranges);
		}

		public bool CanStepOver {
			get { return ProcessState == DebuggerProcessState.Stopped && GetCurrentILFrame() != null; }
		}

		public void StepOver() {
			if (!CanStepOver)
				return;

			var ranges = GetStepRanges(TheDebugger.Debugger, GetCurrentILFrame(), false);
			TheDebugger.Debugger.StepOver(ranges);
		}

		public bool CanStepOut {
			get { return ProcessState == DebuggerProcessState.Stopped && GetCurrentILFrame() != null; }
		}

		public void StepOut() {
			if (!CanStepOut)
				return;

			TheDebugger.Debugger.StepOut(GetCurrentILFrame());
		}

		public bool CanRunTo(CorFrame frame) {
			return ProcessState == DebuggerProcessState.Stopped && TheDebugger.Debugger.CanRunTo(frame);
		}

		public void RunTo(CorFrame frame) {
			if (!CanRunTo(frame))
				return;

			TheDebugger.Debugger.RunTo(frame);
		}

		public bool CanShowNextStatement {
			get { return ProcessState == DebuggerProcessState.Stopped && GetCurrentILFrame() != null; }
		}

		public void ShowNextStatement() {
			if (!CanShowNextStatement)
				return;

			var tab = fileTabManager.GetOrCreateActiveTab();
			if (!TryShowNextStatement(tab.TryGetTextEditorUIContext())) {
				UpdateCurrentMethod();
				JumpToCurrentStatement(tab);
			}
		}

		bool TryShowNextStatement(ITextEditorUIContext uiContext) {
			// Always reset the selected frame
			stackFrameManager.SelectedFrameNumber = 0;
			if (currentLocation == null)
				return false;
			return DebugUtils.MoveCaretTo(uiContext, currentLocation.Value.SerializedDnSpyToken, currentLocation.Value.Offset);
		}

		ITextEditorUIContext TryGetTextEditorUIContext(object parameter) {
			var ctx = parameter as IMenuItemContext;
			if (ctx == null)
				return null;
			if (ctx.CreatorObject.Guid == new Guid(MenuConstants.GUIDOBJ_TEXTEDITORCONTROL_GUID)) {
				var tab = ctx.CreatorObject.Object as IFileTab;
				return tab == null ? null : tab.UIContext as ITextEditorUIContext;
			}
			return null;
		}

		public bool CanSetNextStatement(object parameter) {
			if (!IsDebugging)
				return false;

			SourceCodeMapping mapping;
			string errMsg;
			if (!DebugGetSourceCodeMappingForSetNextStatement(TryGetTextEditorUIContext(parameter), out errMsg, out mapping))
				return false;

			if (currentLocation != null && currentLocation.Value.IsExact)
				return currentLocation.Value.Offset != mapping.ILInstructionOffset.From;
			return true;
		}

		public bool SetNextStatement(object parameter) {
			string errMsg;
			if (!DebugSetNextStatement(parameter, out errMsg)) {
				if (string.IsNullOrEmpty(errMsg))
					errMsg = "Could not set next statement (unknown reason)";
				messageBoxManager.Show(errMsg);
				return false;
			}

			return true;
		}

		bool DebugSetNextStatement(object parameter, out string errMsg) {
			SourceCodeMapping mapping;
			if (!DebugGetSourceCodeMappingForSetNextStatement(TryGetTextEditorUIContext(parameter), out errMsg, out mapping))
				return false;

			uint ilOffset = mapping.ILInstructionOffset.From;
			var ilFrame = GetCurrentMethodILFrame();
			bool failed = ilFrame == null || !ilFrame.SetILFrameIP(ilOffset);

			// All frames are invalidated
			TheDebugger.CallOnProcessStateChanged();

			if (failed) {
				errMsg = "Could not set the next statement.";
				return false;
			}

			return true;
		}

		bool DebugGetSourceCodeMappingForSetNextStatement(ITextEditorUIContext uiContext, out string errMsg, out SourceCodeMapping mapping) {
			errMsg = string.Empty;
			mapping = null;

			if (ProcessState == DebuggerProcessState.Terminated) {
				errMsg = "We're not debugging";
				return false;
			}
			if (ProcessState == DebuggerProcessState.Starting || ProcessState == DebuggerProcessState.Continuing || ProcessState == DebuggerProcessState.Running) {
				errMsg = "Can't set next statement when the process is running";
				return false;
			}

			if (uiContext == null) {
				uiContext = fileTabManager.ActiveTab.TryGetTextEditorUIContext();
				if (uiContext == null) {
					errMsg = "No tab is available. Decompile the current method!";
					return false;
				}
			}

			CodeMappings cm;
			if (currentLocation == null || !DebugUtils.VerifyAndGetCurrentDebuggedMethod(uiContext, currentLocation.Value.SerializedDnSpyToken, out cm)) {
				errMsg = "No debug information found. Make sure that only the debugged method is selected in the treeview (press 'Alt+Num *' to go to current statement)";
				return false;
			}
			Debug.Assert(currentLocation != null);

			var location = uiContext.Location;
			var bps = cm.Find(location.Line, location.Column);
			if (bps.Count == 0) {
				errMsg = "It's not possible to set the next statement here";
				return false;
			}

			if (GetCurrentMethodILFrame() == null) {
				errMsg = "There's no IL frame";
				return false;
			}

			foreach (var bp in bps) {
				var md = bp.MemberMapping.MethodDef;
				if (currentLocation.Value.Function.Token != md.MDToken.Raw)
					continue;
				var serAsm = serializedDnSpyModuleCreator.Create(md.Module);
				if (!serAsm.Equals(currentLocation.Value.SerializedDnSpyToken.Module))
					continue;

				mapping = bp;
				break;
			}
			if (mapping == null) {
				errMsg = "The next statement cannot be set to another method";
				return false;
			}

			return true;
		}
	}
}
