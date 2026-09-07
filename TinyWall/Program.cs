using System;
using System.Threading;
using System.IO;
using System.Net;
using System.ServiceProcess;
using pylorak.Utilities;
using pylorak.TinyWall.Prompting;

namespace pylorak.TinyWall
{
    static class Program
    {
        internal static bool RestartOnQuit { get; set; }
        internal static System.Globalization.CultureInfo? DefaultOsCulture { get; set; }

        private static int StartDevelTool()
        {
            System.Windows.Forms.Application.EnableVisualStyles();
            System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
            System.Windows.Forms.Application.Run(new DevelToolForm());
            return 0;
        }

        private static int StartService(TinyWallService tw)
        {
#if DEBUG
            if (!Utils.RunningAsAdmin())
            {
                Console.WriteLine("Error: Not started as an admin process.");
                return -1;
            }
#endif

            try
            {
                Installer.InstallationSafety.RequireNoTinyWall();
#if !DEBUG
                Installer.InstallationSafety.RequireProtectedInstallation();
#endif
            }
            catch (Exception exception)
            {
                Utils.LogException(exception, Utils.LOG_ID_SERVICE);
                return -1;
            }

            using var SingleInstanceMutex = new Mutex(true, SecureWallProduct.ServiceMutexName, out bool mutexok);
            if (!mutexok)
            {
                return -1;
            }

#if DEBUG
            tw.Start(Array.Empty<string>());
            tw.StartedEvent.WaitOne();
#else
            pylorak.Windows.Services.ServiceBase.Run(tw);
#endif
            return 0;
        }

        private static int StartController(CmdLineArgs opts)
        {
            // Start controller application
            System.Windows.Forms.Application.EnableVisualStyles();
            System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
            do
            {
                RestartOnQuit = false;
                System.Windows.Forms.Application.Run(new TinyWallController(opts));
            } while (RestartOnQuit);
            return 0;
        }

#if DEBUG
        private static int StartPromptPreview()
        {
            System.Windows.Forms.Application.EnableVisualStyles();
            System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
            using var popup = new BlockedConnectionPopup();

            void ExitPreview(object? sender, EventArgs eventArgs)
            {
                popup.ClosePrompt();
                System.Windows.Forms.Application.ExitThread();
            }

            popup.AllowRequested += (sender, eventArgs) =>
                popup.ShowActionFailure(PromptActionStatus.ApplyFailed);
            popup.IgnoreRequested += ExitPreview;
            popup.PromptClosed += ExitPreview;
            popup.PromptTimedOut += ExitPreview;
            // Sample every warning line regardless of what svchost.exe looks like on disk.
            popup.RiskProbe = _ =>
                ExecutableRiskFlags.Unsigned |
                ExecutableRiskFlags.UserWritableLocation |
                ExecutableRiskFlags.RecentlyModified;
            popup.ShowPrompt(new PromptWireDto
            {
                Token = Guid.NewGuid(),
                SubjectKind = PromptIdentityKind.Service,
                CanAllow = true,
                ExecutablePath = @"C:\Windows\System32\svchost.exe",
                ServiceName = "Dnscache",
                FirstSeenUtc = DateTimeOffset.UtcNow,
                LastSeenUtc = DateTimeOffset.UtcNow,
                ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(2),
                RemoteAddress = "1.1.1.1",
                RemotePort = 53,
                Protocol = 17,
                OccurrenceCount = 1,
            });
            System.Windows.Forms.Application.Run();
            return 0;
        }

        private static int RunProtocolSelfTest()
        {
            Guid token = Guid.NewGuid();
            var prompt = new PromptWireDto
            {
                Token = token,
                SubjectKind = PromptIdentityKind.Executable,
                CanAllow = true,
                ExecutablePath = @"C:\apps\sample.exe",
                FirstSeenUtc = DateTimeOffset.UtcNow,
                LastSeenUtc = DateTimeOffset.UtcNow,
                ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(2),
                RemoteAddress = "203.0.113.20",
                RemotePort = 443,
                Protocol = 6,
                OccurrenceCount = 1,
            };
            TwMessage[] messages =
            {
                new TwMessageReadPendingPrompts(new[] { prompt }),
                TwMessagePromptAction.CreateDismissRequest(token),
                TwMessagePromptAction.CreateAllowRequest(token),
            };

            foreach (TwMessage message in messages)
            {
                byte[] bytes = SerializationHelper.Serialize<TwMessage>(message);
                TwMessage copy = SerializationHelper.Deserialize<TwMessage>(bytes, TwMessageComError.Instance);
                if (copy.Type != message.Type)
                    return 1;
                if (copy is TwMessagePromptAction action && action.Token != token)
                    return 1;
                if (copy is TwMessageReadPendingPrompts prompts &&
                    (prompts.Prompts.Length != 1 || prompts.Prompts[0].Token != token))
                {
                    return 1;
                }
            }

            return 0;
        }

        private static int RunPipeIntegrationSelfTest()
        {
            try
            {
                return RunPipeIntegrationSelfTestCore();
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                return 2;
            }
        }

        private static int RunPipeIntegrationSelfTestCore()
        {
            using (var resetProbe = new System.IO.Pipes.NamedPipeServerStream(
                $"SecureWallPipeSelfTest-{Guid.NewGuid():N}"))
            {
                if (PipeServerEndpoint.TryResetForNextClient(resetProbe))
                    return 1; // An unconnected instance must be recreated.
                resetProbe.Dispose();
                if (PipeServerEndpoint.TryResetForNextClient(resetProbe))
                    return 1; // A timed-out/disposed instance must be recreated.
            }
            // Exercise descriptor construction in memory only. This test never applies
            // a DACL to a process and never starts the SYSTEM service.
            byte[] Descriptor(string sddl)
            {
                var value = new System.Security.AccessControl.RawSecurityDescriptor(sddl);
                var bytes = new byte[value.BinaryLength];
                value.GetBinaryForm(bytes, 0);
                return bytes;
            }
            var secured = new System.Security.AccessControl.RawSecurityDescriptor(
                PipeServerIdentity.CreateProcessQueryDescriptor(Descriptor("O:SYG:SYD:(A;;GA;;;SY)(A;;GA;;;BA)")), 0);
            if (secured.DiscretionaryAcl?.Count != 3 ||
                !(secured.DiscretionaryAcl[2] is System.Security.AccessControl.CommonAce added) ||
                added.AceQualifier != System.Security.AccessControl.AceQualifier.AccessAllowed ||
                added.AccessMask != 0x101000 ||
                !added.SecurityIdentifier.IsWellKnown(System.Security.Principal.WellKnownSidType.AuthenticatedUserSid))
                return 1;
            foreach (string denied in new[] { "O:SYG:SYD:(D;;0x1000;;;AU)(A;;GA;;;SY)", "O:SYG:SYD:(D;ID;0x1000;;;AU)(A;;GA;;;SY)" })
            {
                try { PipeServerIdentity.CreateProcessQueryDescriptor(Descriptor(denied)); return 1; }
                catch (UnauthorizedAccessException) { }
            }
            try { PipeServerIdentity.CreateProcessQueryDescriptor(Descriptor("O:SYG:SY")); return 1; }
            catch (InvalidOperationException) { }

            string pipeName = $"SecureWallPipeSelfTest-{Guid.NewGuid():N}";
            Guid token = Guid.NewGuid();
            var prompt = new PromptWireDto
            {
                Token = token,
                SubjectKind = PromptIdentityKind.Executable,
                CanAllow = true,
                ExecutablePath = @"C:\apps\pipe-test.exe",
                FirstSeenUtc = DateTimeOffset.UtcNow,
                LastSeenUtc = DateTimeOffset.UtcNow,
                ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(2),
                RemoteAddress = "203.0.113.20",
                RemotePort = 443,
                Protocol = 6,
                OccurrenceCount = 1,
            };
            int requestCount = 0;

            TwMessage HandleRequest(TwMessage request)
            {
                Interlocked.Increment(ref requestCount);
                if (request is TwMessageReadPendingPrompts readRequest)
                    return readRequest.CreateResponse(new[] { prompt });
                if (request is TwMessagePromptAction action)
                {
                    if (action.Token != token)
                        return action.CreateResponse(PromptActionStatus.UnknownToken);
                    return action.CreateResponse(
                        action.Type == MessageType.ALLOW_PROMPT
                            ? PromptActionStatus.Allowed
                            : PromptActionStatus.Dismissed);
                }

                return TwMessageError.Instance;
            }

            using var server = new PipeServerEndpoint(HandleRequest, pipeName);
            for (int cycle = 0; cycle < 3; cycle++)
            {
                if (new Controller(pipeName).BeginReadPendingPrompts().Response.Type != MessageType.COM_ERROR || requestCount != cycle * 4)
                    return 1;
                var controller = new Controller(pipeName, System.Diagnostics.Process.GetCurrentProcess().Id);
                PromptWireDto[] prompts = Controller.EndReadPendingPrompts(
                    controller.BeginReadPendingPrompts().Response);
                PromptActionStatus allow = controller.AllowPrompt(token);
                PromptActionStatus dismiss = controller.DismissPrompt(token);
                PromptActionStatus unknown = controller.AllowPrompt(Guid.NewGuid());
                if (prompts.Length != 1 || prompts[0].Token != token ||
                    prompts[0].ExecutablePath != prompt.ExecutablePath ||
                    allow != PromptActionStatus.Allowed || dismiss != PromptActionStatus.Dismissed ||
                    unknown != PromptActionStatus.UnknownToken || requestCount != (cycle + 1) * 4)
                {
                    Console.Error.WriteLine($"Pipe self-test failed: cycle={cycle}, prompts={prompts.Length}, allow={allow}, dismiss={dismiss}, unknown={unknown}, requests={requestCount}");
                    Console.Error.WriteLine(PipeClientEndpoint.DebugSelfTestFailures);
                    return 1;
                }
            }
            Console.Error.WriteLine($"Pipe self-test passed: 3 rejection/recovery cycles, requests={requestCount}");
            return 0;
        }
#endif

        private static int InstallService()
        {
            return TinyWallDoctor.EnsureServiceInstalledAndRunning(Utils.LOG_ID_INSTALLER, true) ? 0 : -1;
        }

        private static int UninstallService()
        {
            return TinyWallDoctor.Uninstall();
        }

        /// <summary>
        /// Der Haupteinstiegspunkt für die Anwendung.
        /// </summary>
        [STAThread]
        static int Main(string[] args)
        {
            HierarchicalStopwatch.Enable = File.Exists(Path.Combine(Utils.AppDataPath, "enable-timings"));
            HierarchicalStopwatch.LogFileBase = Path.Combine(Utils.AppDataPath, @"logs\timings");

            DefaultOsCulture ??= Thread.CurrentThread.CurrentUICulture;

            // WerAddExcludedApplication will fail every time we are not running as admin,
            // so wrap it around a try-catch.
            try
            {
                // Prevent Windows Error Reporting running for us
                Utils.SafeNativeMethods.WerAddExcludedApplication(Utils.ExecutablePath, true);
            }
            catch { }

            // Setup TLS 1.2 & 1.3 support, if supported
            if (ServicePointManager.SecurityProtocol != 0)
            {
                try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
                try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls13; } catch { }
            }

            // Parse comman-line options
            // Explicit maintenance mode overrides the noninteractive service default.
            if (Utils.StringArrayContains(args, "/msi-cleanup"))
                return TinyWallDoctor.UninstallForMsi();
            if (Utils.StringArrayContains(args, "/msi-rollback-install"))
                return TinyWallDoctor.RollbackFailedInstallForMsi();

            var opts = new CmdLineArgs();
            if (!Environment.UserInteractive || Utils.StringArrayContains(args, "/service"))
                opts.ProgramMode = StartUpMode.Service;
            if (Utils.StringArrayContains(args, "/selfhosted"))
                opts.ProgramMode = StartUpMode.SelfHosted;
            if (Utils.StringArrayContains(args, "/develtool"))
                opts.ProgramMode = StartUpMode.DevelTool;
            if (Utils.StringArrayContains(args, "/install"))
                opts.ProgramMode = StartUpMode.Install;
            if (Utils.StringArrayContains(args, "/uninstall"))
                opts.ProgramMode = StartUpMode.Uninstall;
#if DEBUG
            if (Utils.StringArrayContains(args, "/promptpreview"))
                opts.ProgramMode = StartUpMode.PromptPreview;
            if (Utils.StringArrayContains(args, "/protocolselftest"))
                opts.ProgramMode = StartUpMode.ProtocolSelfTest;
            if (Utils.StringArrayContains(args, "/pipeintegrationtest"))
                opts.ProgramMode = StartUpMode.PipeIntegrationSelfTest;
#endif

            if (opts.ProgramMode == StartUpMode.Invalid)
                opts.ProgramMode = StartUpMode.Controller;

            opts.autowhitelist = Utils.StringArrayContains(args, "/autowhitelist");
            opts.updatenow = Utils.StringArrayContains(args, "/updatenow");
            opts.startup = Utils.StringArrayContains(args, "/startup");

#if !DEBUG
            // Register an unhandled exception handler - lol

            void UnhandledException_Gui(object sender, UnhandledExceptionEventArgs e)
            {
                Utils.LogException((Exception)e.ExceptionObject, Utils.LOG_ID_GUI);
            }
            void UnhandledException_Service(object sender, UnhandledExceptionEventArgs e)
            {
                Utils.LogException((Exception)e.ExceptionObject, Utils.LOG_ID_SERVICE);
            }
            void UnhandledException_Installer(object sender, UnhandledExceptionEventArgs e)
            {
                Utils.LogException((Exception)e.ExceptionObject, Utils.LOG_ID_INSTALLER);
            }

            switch (opts.ProgramMode)
            {
                case StartUpMode.Install:
                    AppDomain.CurrentDomain.UnhandledException += UnhandledException_Installer;
                    break;
                case StartUpMode.Uninstall:
                    AppDomain.CurrentDomain.UnhandledException += UnhandledException_Installer;
                    break;
                case StartUpMode.Controller:
                    AppDomain.CurrentDomain.UnhandledException += UnhandledException_Gui;
                    break;
                case StartUpMode.DevelTool:
                    AppDomain.CurrentDomain.UnhandledException += UnhandledException_Gui;
                    break;
                case StartUpMode.SelfHosted:
                    AppDomain.CurrentDomain.UnhandledException += UnhandledException_Gui;
                    AppDomain.CurrentDomain.UnhandledException += UnhandledException_Service;
                    break;
                case StartUpMode.Service:
                    AppDomain.CurrentDomain.UnhandledException += UnhandledException_Service;
                    break;
            }
#endif


            switch (opts.ProgramMode)
            {
                case StartUpMode.Install:
                    return InstallService();
                case StartUpMode.Uninstall:
                    return UninstallService();
                case StartUpMode.Controller:
                    return StartController(opts);
#if DEBUG
                case StartUpMode.PromptPreview:
                    return StartPromptPreview();
                case StartUpMode.ProtocolSelfTest:
                    return RunProtocolSelfTest();
                case StartUpMode.PipeIntegrationSelfTest:
                    return RunPipeIntegrationSelfTest();
#endif
                case StartUpMode.DevelTool:
                    return StartDevelTool();
                case StartUpMode.SelfHosted:
                    using (var srv = new TinyWallService())
                    {
                        int startResult = StartService(srv);
                        if (startResult != 0) return startResult;
                        int ret = StartController(opts);
                        srv.Stop();
                        srv.StoppedEvent.WaitOne();
                        return ret;
                    }
                case StartUpMode.Service:
                    using (var srv = new TinyWallService())
                    {
#if !DEBUG
                        pylorak.Windows.PathMapper.Instance.AutoUpdate = false;
#endif
                        int startResult = StartService(srv);
                        if (startResult != 0) return startResult;
#if DEBUG
                        Console.WriteLine("Kill process to terminate...");
                        srv.StoppedEvent.WaitOne();
#endif
                    }
                    return 0;
                default:
                    return -1;
            } // switch
        } // Main

    } // class
} //namespace
