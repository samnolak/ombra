using System.Text;
using OmpGui.Rpc;

namespace OmpGui.ClientCore;

/// <summary>
/// Names the app's chats the way omp's own terminal does. omp 18.8.0 generates a session title from the first message
/// only in its terminal UI (input-controller.ts) and for a message on its command line (main.ts); a prompt over RPC
/// never starts it, so every chat the app opened stayed "New session" or its first words. The app passes omp an
/// extension (<c>--extension</c>, like the browser note) whose <c>input</c> handler, for a message sent over RPC while
/// the session has no name, runs omp's own title generator (<c>utils/title-generator.ts</c>: the user's tiny / commit /
/// smol model, the local tiny model when that is chosen, TITLE_SYSTEM.md is not consulted) on the session's first
/// message — or this one, when the first said too little — and sets the name (<c>setSessionName</c>). It never
/// waits: the message goes on at once, the name lands a moment later and the app reads it when the reply ends
/// (get_state). A name the user gave is never replaced. Not gated on <c>PI_NO_TITLE</c>: omp sets it itself for every
/// RPC run (main.ts), which is exactly what this undoes for the app's chats.
/// </summary>
public sealed class SessionTitles(string directory)
{
    public const string ExtensionFileName = "omp-gui-titles.js";
    private const string ExtensionFlag = "--extension";
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private readonly Lock _gate = new();

    public string ExtensionPath { get; } = Path.Combine(directory, ExtensionFileName);

    /// <summary>A chat's omp with the extension; unchanged when the file cannot be written (a line goes to stderr).</summary>
    public OmpLaunchSpec Apply(OmpLaunchSpec spec)
    {
        if (!TryWrite()) return spec;
        return spec with { Arguments = [.. spec.Arguments, ExtensionFlag, ExtensionPath] };
    }

    public const string ExtensionSource = """
        // Written by OMP GUI for the chats it starts: names an unnamed session with omp's own title generator, as omp's
        // terminal does (omp does not do it for prompts sent over RPC). A name you gave is never replaced.
        import { generateSessionTitle } from "@oh-my-pi/pi-coding-agent/utils/title-generator";
        import { Settings } from "@oh-my-pi/pi-coding-agent/config/settings";

        function textOf(content) {
        	if (typeof content === "string") return content;
        	if (!Array.isArray(content)) return "";
        	return content.filter(part => part && part.type === "text" && typeof part.text === "string").map(part => part.text).join("");
        }

        function firstUserText(sessionManager) {
        	try {
        		for (const entry of sessionManager.getBranch()) {
        			if (entry.type === "message" && entry.message && entry.message.role === "user") {
        				const text = textOf(entry.message.content).trim();
        				if (text) return text;
        			}
        		}
        	} catch {}
        	return "";
        }

        export default function ompGuiTitles(pi) {
        	let naming = false;
        	pi.on("input", (event, ctx) => {
        		if (event.source !== "rpc" || naming || pi.getSessionName()) return undefined;
        		const text = (event.text || "").trim();
        		if (!text || text.startsWith("/") || text.startsWith("!")) return undefined;
        		naming = true;
        		void (async () => {
        			try {
        				const settings = Settings.instance;
        				const sessionId = ctx.sessionManager.getSessionId();
        				const first = firstUserText(ctx.sessionManager);
        				let title = first && first !== text
        					? await generateSessionTitle(first, ctx.modelRegistry, settings, sessionId, ctx.model)
        					: null;
        				if (!title) title = await generateSessionTitle(text, ctx.modelRegistry, settings, sessionId, ctx.model);
        				if (title && !pi.getSessionName()) await pi.setSessionName(title.trim());
        			} catch (error) {
        				console.error("OMP GUI: no session title: " + (error && error.message ? error.message : error));
        			} finally {
        				naming = false;
        			}
        		})();
        		return undefined;
        	});
        }

        """;

    private bool TryWrite()
    {
        lock (_gate)
        {
            try
            {
                var dir = Path.GetDirectoryName(ExtensionPath)!;
                if (OperatingSystem.IsWindows()) Directory.CreateDirectory(dir);
                else Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                if (File.Exists(ExtensionPath) && File.ReadAllText(ExtensionPath, Utf8) == ExtensionSource) return true;
                var temp = ExtensionPath + ".tmp";
                File.WriteAllText(temp, ExtensionSource, Utf8);
                File.Move(temp, ExtensionPath, overwrite: true);
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine("The session-title extension could not be written (" + e.Message + "); chats keep their first words as names.");
                return false;
            }
        }
    }
}
