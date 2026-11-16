using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq.Expressions;
using System.Net;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using CapriDesktop;

internal static class GameControlTests
{
    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static int checks, calls, seerCalls;
    private static object lastArgs, nextPicture, sight, job, companion;
    private static bool nativeThrows;
    private static string nextReply;
    private static Assembly capri;
    private static Type computer, argsType, pictureType, sightType, jobType;
    private static DarkPrinceGameControl mod;

    private static void Check(bool ok, string name)
    {
        if (!ok) throw new Exception(name);
        checks++;
        Console.WriteLine("PASS: " + name);
    }
    private static void Near(double a, double b, string name) { Check(Math.Abs(a - b) < .00001, name); }
    private static FieldInfo Field(Type type, string name)
    {
        for (Type t = type; t != null; t = t.BaseType)
        {
            FieldInfo f = t.GetField(name, Any | BindingFlags.DeclaredOnly);
            if (f != null) return f;
        }
        throw new Exception("Missing fixture field: " + name);
    }
    private static object Get(object o, string name) { return Field(o.GetType(), name).GetValue(o); }
    private static void Set(object o, string name, object value) { Field(o.GetType(), name).SetValue(o, value); }
    private static object New(Type t) { return FormatterServices.GetUninitializedObject(t); }
    private static Delegate Typed(Type type, Func<object[], object> body)
    {
        ParameterInfo[] args = type.GetMethod("Invoke").GetParameters();
        ParameterExpression[] parameters = new ParameterExpression[args.Length];
        Expression[] boxed = new Expression[args.Length];
        for (int i = 0; i < args.Length; i++) { parameters[i] = Expression.Parameter(args[i].ParameterType, "p" + i); boxed[i] = Expression.Convert(parameters[i], typeof(object)); }
        return Expression.Lambda(type, Expression.Convert(Expression.Invoke(Expression.Constant(body), Expression.NewArrayInit(typeof(object), boxed)), type.GetMethod("Invoke").ReturnType), parameters).Compile();
    }
    private static object Invoke(string slot, params object[] values) { return ((Delegate)Field(computer, slot).GetValue(null)).DynamicInvoke(values); }
    private static string Json(string extra)
    {
        return "GC_OBS {\"target\":\"blue flag\",\"x\":50,\"y\":40,\"w\":10,\"h\":20,\"range\":\"near\",\"intent\":\"approach\",\"confidence\":0.9,\"fixed\":true,\"hazard\":false" + extra + "}\nwalk{\"keys\":\"w\",\"seconds\":\"1\"}";
    }

    private static void Parsing()
    {
        var o = DarkPrinceGameControl.ReadObservation(Json(",\"distance\":12,\"unit\":\"m\",\"stop\":3"));
        Check(o != null && o.Visible && o.Target == "blue flag", "A valid current-target observation parses.");
        Check(o.Distance == 12 && o.Stop == 3 && o.Unit == "m", "HUD readings retain their explicit unit.");
        Check(DarkPrinceGameControl.StripObservation(Json("")) == "walk{\"keys\":\"w\",\"seconds\":\"1\"}", "Private metadata is removed without changing native moves.");
        Check(DarkPrinceGameControl.ReadObservation(Json(",\"x\":60")) == null, "Duplicate observation keys are rejected.");
        Check(DarkPrinceGameControl.ReadObservation(Json("").Replace("\"x\":50", "\"x\":105")) == null, "Off-screen target boxes are rejected.");
        Check(DarkPrinceGameControl.ReadObservation(Json("").Replace("\"h\":20", "\"h\":0")) == null, "Zero-size boxes are rejected.");
        Check(DarkPrinceGameControl.ReadObservation(Json("").Replace("\"x\":50", "\"x\":1e999")) == null, "Non-finite numeric readings are rejected.");
        Check(DarkPrinceGameControl.ReadObservation(Json("").Replace("\"confidence\":0.9", "\"confidence\":2")) == null, "Out-of-range confidence is rejected.");
        Check(DarkPrinceGameControl.ReadObservation(Json("").Replace("\"intent\":\"approach\"", "\"intent\":\"buy\"")) == null, "Unknown movement intent is rejected.");
        Check(DarkPrinceGameControl.ReadObservation(Json("") + "\n" + Json("")) == null, "Conflicting duplicate observation lines are rejected.");
        Check(DarkPrinceGameControl.FlatJson("{\"x\":5,}") == null, "A trailing JSON comma is rejected.");
        Check(DarkPrinceGameControl.FlatJson("{\"x\":5} rubbish") == null, "Trailing data after metadata is rejected.");
        Check(DarkPrinceGameControl.FlatJson("{\"x\":{\"nested\":5}}") == null, "Nested metadata cannot masquerade as scalar values.");
        Check(DarkPrinceGameControl.FlatJson("{\"target\":\"blue\\u0020flag\"}")["target"] == "blue flag", "Escaped labels are decoded.");
        Check(DarkPrinceGameControl.FlatJson("{\"target\":\"" + new string('x', 1900) + "\"}") == null, "Oversized metadata is rejected.");
        var lost = DarkPrinceGameControl.ReadObservation("GC_OBS {\"target\":\"\",\"game\":\"Jujutsu Shenanigans\",\"confidence\":0.9}\n");
        Check(lost != null && !lost.Visible && lost.Game == "Jujutsu Shenanigans", "A menu can identify the game without inventing a target.");
        o = DarkPrinceGameControl.ReadObservation(Json(",\"distance\":-1,\"unit\":\"m\",\"stop\":3"));
        Check(o != null && !o.Distance.HasValue && !o.Stop.HasValue, "Invalid world-distance values are discarded.");
        o = DarkPrinceGameControl.ReadObservation(Json(",\"distance\":12,\"unit\":\"pixels\",\"stop\":3"));
        Check(!o.Distance.HasValue && o.Unit == "", "Pixel distances are not treated as world distance.");
        string embedded = "GC_OBS {\"target\":\"walk{\\\"keys\\\":\\\"w\\\"}\"}\npress{\"keys\":\"1\"}";
        Check(DarkPrinceGameControl.StripObservation(embedded) == "press{\"keys\":\"1\"}", "Command-looking text inside metadata never becomes a native move.");
    }

    private static void Movement()
    {
        string why;
        var o = DarkPrinceGameControl.ReadObservation(Json(""));
        Near(DarkPrinceGameControl.ApproachLimit(o, 1.25, 0, 0, 0, 1, out why), .3, "Near-target movement is shortened.");
        o.Range = "mid";
        Near(DarkPrinceGameControl.ApproachLimit(o, 1.25, 0, 0, 0, 1, out why), .75, "Middle-range movement has an intermediate cap.");
        o.Range = "far";
        Near(DarkPrinceGameControl.ApproachLimit(o, 1.25, 0, 0, 0, 1, out why), 2.5, "Clear distant fixed goals permit longer requested steps.");
        Near(DarkPrinceGameControl.ApproachLimit(o, 3, 0, 0, 0, 1, out why), 3, "Adaptive travel remains bounded to three seconds.");
        o.Fixed = false;
        Near(DarkPrinceGameControl.ApproachLimit(o, 1.25, 0, 0, 0, 1, out why), 1.25, "A moving player does not receive a distant-scenery extension.");
        Near(DarkPrinceGameControl.ApproachLimit(o, 1.25, 0, 0, 0, 4, out why), .3, "Older observations of moving targets are treated cautiously.");
        o.Fixed = true; o.Range = "ready";
        Near(DarkPrinceGameControl.ApproachLimit(o, 1.25, 0, 0, 0, 1, out why), 0, "Approach stops at a reported visible range criterion.");
        o.Intent = "retreat";
        Near(DarkPrinceGameControl.ApproachLimit(o, 1.25, 0, 0, 0, 1, out why), 1.25, "Reaching a goal does not suppress retreat.");
        o.Intent = "approach"; o.Range = "far"; o.Hazard = true;
        Near(DarkPrinceGameControl.ApproachLimit(o, 1.25, 0, 0, 0, 1, out why), .3, "Ledges and uncertain paths shorten movement.");
        o.Hazard = false;
        Near(DarkPrinceGameControl.ApproachLimit(o, 1.25, 0, 0, 0, 20, out why), .3, "Stale stationary-target observations cannot authorize long steps.");
        o.Distance = 6; o.Stop = 5;
        Near(DarkPrinceGameControl.ApproachLimit(o, 1.25, 4, 2, 0, 1, out why), .15, "Two consistent HUD calibration samples cap approach before the goal.");
        Near(DarkPrinceGameControl.ApproachLimit(o, 1.25, 4, 1, 0, 1, out why), 2.5, "One HUD delta is insufficient to calibrate movement.");
        o.Distance = null; o.Stop = null;
        Near(DarkPrinceGameControl.ApproachLimit(o, 1.25, 0, 0, 1, 1, out why), .18, "Apparent expansion shortens approach without inventing metres.");
        o.Fixed = false;
        Near(DarkPrinceGameControl.ApproachLimit(o, 1.25, 0, 0, 1, 1, out why), 1.25, "A moving player is not used for stationary scale calibration.");
        var a = DarkPrinceGameControl.ReadObservation(Json(""));
        var b = DarkPrinceGameControl.ReadObservation(Json(""));
        Check(DarkPrinceGameControl.SameFixedTarget(a, b), "Matching fixed visible landmarks can be compared.");
        b.Target = "other flag";
        Check(!DarkPrinceGameControl.SameFixedTarget(a, b), "Switching targets invalidates calibration.");
        b.Target = a.Target; b.Hazard = true;
        Check(!DarkPrinceGameControl.SameFixedTarget(a, b), "Hazardous views invalidate calibration.");
    }

    private static void Research()
    {
        string item = "<item><title>Jujutsu Shenanigans controls</title><link>https://example.org/game-guide</link><description>Jujutsu Shenanigans keyboard controls and beginner objectives.</description></item>";
        string parsed = DarkPrinceGameControl.ParseSearch("<rss><channel>" + item + item + "</channel></rss>", "Jujutsu Shenanigans", true);
        Check(parsed.Contains("https://example.org/game-guide"), "Research preserves a source URL.");
        Check(parsed.Split('\n').Length == 1, "Duplicate search references are removed.");
        Check(DarkPrinceGameControl.ParseSearch(item, "Counter Strike", true) == "", "Unrelated search results are rejected.");
        Check(DarkPrinceGameControl.ParseSearch(item, "Roblox", true) == "", "A generic Roblox title is not treated as an exact experience.");
        Check(DarkPrinceGameControl.ParseSearch(item.Replace("https://example.org/game-guide", "javascript:alert(1)"), "Jujutsu Shenanigans", true) == "", "Non-web source schemes are rejected.");
        Check(DarkPrinceGameControl.ParseSearch(item.Replace("https://example.org/game-guide", "http://127.0.0.1/private"), "Jujutsu Shenanigans", true) == "", "Local IP sources are not presented as public guides.");
        Check(DarkPrinceGameControl.ParseSearch(item.Replace("https://example.org/game-guide", "https://user:secret@example.org/"), "Jujutsu Shenanigans", true) == "", "Credential-bearing source URLs are rejected.");
        string html = "<a class=\"result__a\" href=\"//duckduckgo.com/l/?uddg=https%3A%2F%2Fexample.org%2Fcontrols\">Jujutsu Shenanigans controls</a><a class=\"result__snippet\">Jujutsu Shenanigans controls &amp; beginner objectives to read.</a>";
        parsed = DarkPrinceGameControl.ParseSearch(html, "Jujutsu Shenanigans", false);
        Check(parsed.Contains("https://example.org/controls") && parsed.Contains("controls & beginner"), "HTML search links and entities are decoded.");
        Check(!DarkPrinceGameControl.ParseSearch(item.Replace("keyboard controls", "<script>doBadThings()</script>keyboard controls"), "Jujutsu Shenanigans", true).Contains("doBadThings"), "Script content is excluded from reference snippets.");
        Check(DarkPrinceGameControl.ParseSearch(new string('x', 192001), "Jujutsu Shenanigans", true) == "", "Oversized responses are rejected.");
    }

    private static void Images()
    {
        byte[] fallback = new byte[] { 1, 2, 3 };
        Check(object.ReferenceEquals(DarkPrinceGameControl.Sharpened(null, new Size(1280, 720), fallback), fallback), "No sharper source leaves the existing screenshot untouched.");
        byte[] input;
        using (Bitmap image = new Bitmap(2560, 1440))
        using (Graphics paint = Graphics.FromImage(image))
        using (MemoryStream buffer = new MemoryStream())
        { paint.Clear(Color.DarkBlue); image.Save(buffer, System.Drawing.Imaging.ImageFormat.Png); input = buffer.ToArray(); }
        byte[] result = DarkPrinceGameControl.Sharpened(input, new Size(1280, 720), fallback);
        using (MemoryStream stream = new MemoryStream(result))
        using (Bitmap image = new Bitmap(stream))
            Check(image.Width == 1600 && image.Height == 900, "Sharper frames preserve the full-view aspect and stay within 1600 pixels.");
        Check(object.ReferenceEquals(DarkPrinceGameControl.Sharpened(input, new Size(1000, 1000), fallback), fallback), "An incompatible image aspect cannot corrupt coordinate mapping.");
    }

    private static object Args(string keys, string seconds)
    {
        object a = New(argsType); Set(a, "Keys", keys); Set(a, "Seconds", seconds); return a;
    }
    private static void Frame()
    {
        nextPicture = New(pictureType);
        Set(nextPicture, "Size", new Size(1280, 720)); Set(nextPicture, "Area", new Rectangle(0, 0, 1280, 720)); Set(nextPicture, "Sight", sight);
        object captured = Invoke("Shoot", new object[] { null });
        Set(job, "shown", captured); Field(computer, "Shown").SetValue(null, captured);
    }
    private static string Observe(string response)
    {
        nextReply = response;
        var notes = (Func<string, string>)Field(capri.GetType("CapriDesktop.GameNotes", true), "Skills").GetValue(null);
        string instructions = notes("Jujutsu Shenanigans");
        Check(instructions.Contains("existing skill") && instructions.Contains("GC_OBS"), "Existing game skills and the new observation protocol coexist.");
        return ((Func<byte[], string, string, string>)Get(job, "Seer"))(new byte[] { 1 }, instructions, "What next?");
    }

    private static void NativeIntegration()
    {
        capri = typeof(ICapriHost).Assembly;
        computer = capri.GetType("CapriDesktop.Computer", true);
        argsType = capri.GetType("CapriDesktop.ToolArguments", true); pictureType = capri.GetType("CapriDesktop.Picture", true);
        sightType = capri.GetType("CapriDesktop.Sight", true); jobType = capri.GetType("CapriDesktop.Job", true);
        companion = New(capri.GetType("CapriDesktop.Companion", true)); job = New(jobType); Set(companion, "job", job);
        jobType.GetProperty("Playing", Any).GetSetMethod(true).Invoke(job, new object[] { true });
        sight = New(sightType); Set(sight, "Window", new IntPtr(42)); Set(sight, "Title", "Roblox"); Set(sight, "Program", "RobloxPlayerBeta"); Set(sight, "Bounds", new Rectangle(0, 0, 1280, 720));
        Field(computer, "Front").SetValue(null, (Func<IntPtr>)delegate { return new IntPtr(42); });
        Field(computer, "interrupted").SetValue(null, false);
        Field(computer, "why").SetValue(null, "You pressed Esc, so I stopped.");
        Delegate fakeAct = Typed(Field(computer, "Act").FieldType, delegate(object[] p)
        {
            calls++; lastArgs = p[1];
            if (nativeThrows) throw new InvalidOperationException("Native fixture error after action.");
            return (string)p[0] == "walk" ? "Moved for " + (string)Get(p[1], "Seconds") + " s." : "Native action.";
        });
        Delegate fakeUse = Typed(Field(computer, "Use").FieldType, delegate(object[] p) { calls++; lastArgs = p[1]; return (string)p[0] == "left_mouse_up" ? "Released." : "Native member."; });
        Delegate fakeShoot = Typed(Field(computer, "Shoot").FieldType, delegate(object[] p) { return nextPicture; });
        Field(computer, "Act").SetValue(null, fakeAct); Field(computer, "Use").SetValue(null, fakeUse); Field(computer, "Shoot").SetValue(null, fakeShoot);
        Func<string, string> oldSkills = delegate { return "existing skill"; };
        Field(capri.GetType("CapriDesktop.GameNotes", true), "Skills").SetValue(null, oldSkills);
        Func<byte[], string, string, string> oldSeer = delegate { seerCalls++; return nextReply; };
        Set(job, "Seer", oldSeer);
        mod = new DarkPrinceGameControl();
        StubHost host = new StubHost((System.Windows.Forms.Form)companion);
        host.Data.Set("research", false); host.Data.Set("sharper", false);
        mod.Start(host);
        Frame();
        string answer = Observe(Json(""));
        Check(seerCalls == 1 && !answer.Contains("GC_OBS") && answer.Contains("walk{"), "The existing vision call runs once and returns only native moves.");
        object original = Args("w", "5"); int before = calls;
        string result = (string)Invoke("Act", "walk", original, sight);
        Check(calls == before + 1 && (string)Get(lastArgs, "Seconds") == "0.3", "Near-target limits reach the real typed native delegate.");
        Check((string)Get(original, "Seconds") == "5" && !object.ReferenceEquals(original, lastArgs), "The original native tool arguments remain unchanged.");
        before = calls; result = (string)Invoke("Act", "walk", Args("w", "1"), sight);
        Check(calls == before && result.StartsWith("Couldn't"), "A second navigation move from one screenshot is withheld.");
        object skill = Args("1", "2"); result = (string)Invoke("Act", "press", skill, sight);
        Check(object.ReferenceEquals(lastArgs, skill), "Skill button timing and arguments pass through unchanged.");
        Frame(); Observe(Json("").Replace("\"range\":\"near\"", "\"range\":\"ready\""));
        before = calls; result = (string)Invoke("Act", "walk", Args("w", "1"), sight);
        Check(calls == before && result.StartsWith("Couldn't"), "A reached approach criterion sends no forward input.");
        Frame(); Observe(Json("").Replace("\"range\":\"near\"", "\"range\":\"far\""));
        result = (string)Invoke("Act", "walk", Args("w", "5"), sight);
        Check((string)Get(lastArgs, "Seconds") == "2.5", "A distant stationary goal is not artificially capped to the old 1.25-second limit.");
        Frame(); Observe(Json("").Replace("\"range\":\"near\"", "\"range\":\"far\""));
        Invoke("Act", "walk", Args("w", "0.2"), sight);
        Check((string)Get(lastArgs, "Seconds") == "0.2", "The mod never lengthens a deliberately short native request.");
        Field(computer, "interrupted").SetValue(null, true);
        before = calls; result = (string)Invoke("Act", "press", Args("1", "1"), sight);
        Check(calls == before && result == "You pressed Esc, so I stopped.", "Escape's exact interruption sentinel is preserved.");
        result = (string)Invoke("Use", "left_mouse_up", Args("", ""), nextPicture);
        Check(result == "Released." && calls == before + 1, "Mouse release still passes through during interruption.");
        Field(computer, "interrupted").SetValue(null, false);
        Field(computer, "Front").SetValue(null, (Func<IntPtr>)delegate { return new IntPtr(99); });
        before = calls; result = (string)Invoke("Act", "walk", Args("w", "1"), sight);
        Check(calls == before && result.Contains("focus"), "Unfocused games receive no new movement input.");
        Field(computer, "Front").SetValue(null, (Func<IntPtr>)delegate { return new IntPtr(42); });
        Frame(); Observe(Json(""));
        nativeThrows = true; before = calls;
        try { Invoke("Act", "press", Args("1", "1"), sight); } catch (TargetInvocationException) { }
        Check(calls == before + 1, "A native exception never replays an action.");
        nativeThrows = false;
        Delegate retained = (Delegate)Field(computer, "Act").GetValue(null);
        mod.Stop();
        Check(object.ReferenceEquals(Field(computer, "Act").GetValue(null), fakeAct) && object.ReferenceEquals(Field(computer, "Use").GetValue(null), fakeUse), "Stopping restores the exact previous input delegates.");
        Check(object.ReferenceEquals(Field(computer, "Shoot").GetValue(null), fakeShoot) && object.ReferenceEquals(Get(job, "Seer"), oldSeer), "Stopping restores capture and vision delegates.");
        Check(object.ReferenceEquals(Field(capri.GetType("CapriDesktop.GameNotes", true), "Skills").GetValue(null), oldSkills), "Stopping preserves the user's original game-skill provider.");
        retained.DynamicInvoke("walk", Args("w", "5"), sight);
        Check((string)Get(lastArgs, "Seconds") == "5", "A retained disabled wrapper forwards unchanged.");
    }

    private static void SearchSmoke()
    {
        string game = "Jujutsu Shenanigans";
        string query = Uri.EscapeDataString("\"" + game + "\" PC controls objectives beginner guide");
        string[] urls = { "https://www.bing.com/search?format=rss&q=" + query, "https://html.duckduckgo.com/html/?q=" + query };
        int count = 0;
        for (int i = 0; i < urls.Length && count == 0; i++)
        {
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(urls[i]);
                request.Timeout = 6500; request.ReadWriteTimeout = 3000; request.AllowAutoRedirect = false;
                request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                request.UserAgent = "CapriGameControl/1.1 (public game guide snippets)";
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (StreamReader reader = new StreamReader(response.GetResponseStream()))
                {
                    char[] buffer = new char[192001]; int used = 0;
                    while (used < buffer.Length) { int read = reader.Read(buffer, used, buffer.Length - used); if (read == 0) break; used += read; }
                    string parsed = DarkPrinceGameControl.ParseSearch(new string(buffer, 0, used), game, i == 0);
                    count = parsed.Length == 0 ? 0 : parsed.Split('\n').Length;
                    Console.WriteLine("SEARCH_SMOKE endpoint=" + new Uri(urls[i]).Host + " http=" + (int)response.StatusCode + " matching_snippets=" + count);
                }
            }
            catch (Exception error) { Console.WriteLine("SEARCH_SMOKE unavailable: " + error.GetType().Name); }
        }
        Console.WriteLine(count > 0 ? "SEARCH_SMOKE success: public matching snippets retrieved; full guides and user-network access are not verified." :
            "SEARCH_SMOKE limit: public search was blocked or returned no matching snippets; the mod must fall back to local game notes.");
    }

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length > 0 && args[0] == "--search-smoke") { SearchSmoke(); return 0; }
            Parsing(); Movement(); Research(); Images(); NativeIntegration();
            Console.WriteLine("SUCCESS: " + checks + " behavioral checks using the official 0.8.9 types. No game inputs were sent.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error.ToString()); return 1; }
        finally { if (mod != null) mod.Stop(); }
    }

    private sealed class StubStore : ICapriStore
    {
        private readonly Dictionary<string, string> data = new Dictionary<string, string>();
        public string[] Keys { get { return new List<string>(data.Keys).ToArray(); } }
        public string Get(string key) { return Get(key, ""); }
        public string Get(string key, string fallback) { string value; return data.TryGetValue(key, out value) ? value : fallback; }
        public double Number(string key, double fallback) { double n; return double.TryParse(Get(key), out n) ? n : fallback; }
        public bool Flag(string key, bool fallback) { bool b; return bool.TryParse(Get(key), out b) ? b : fallback; }
        public void Set(string key, string value) { data[key] = value; }
        public void Set(string key, double value) { data[key] = value.ToString(System.Globalization.CultureInfo.InvariantCulture); }
        public void Set(string key, bool value) { data[key] = value.ToString(); }
        public bool Remove(string key) { return data.Remove(key); }
    }
    private sealed class StubUi : ICapriUi
    {
        private readonly System.Windows.Forms.Form window;
        internal StubUi(System.Windows.Forms.Form value) { window = value; }
        public event Action<CapriFrame> Drawing { add { } remove { } }
        public void Redraw() { }
        public void AddMenuItem(string text, Action action) { }
        public void SetOptions(string title, Func<System.Windows.Forms.Control> make) { }
        public void Bubble(string text) { }
        public System.Windows.Forms.Form Window { get { return window; } }
    }
    private sealed class StubHost : ICapriHost
    {
        internal readonly StubStore Data = new StubStore();
        private readonly StubUi ui;
        internal StubHost(System.Windows.Forms.Form form) { ui = new StubUi(form); }
        public string Folder { get { return Path.GetTempPath(); } }
        public void Say(string line) { }
        public void Log(string line) { }
        public Bitmap Art(string name) { return null; }
        public void PlayIntro() { }
        public void Sound(string name) { }
        public int Api { get { return 3; } }
        public string CapriVersion { get { return "0.8.9"; } }
        public bool Has(string permission) { return permission == "tools" || permission == "screen"; }
        public ICapriChat Chat { get { return null; } }
        public ICapriMemory Memory { get { return null; } }
        public ICapriUi Ui { get { return ui; } }
        public ICapriMainframe Mainframe { get { return null; } }
        public ICapriScreen Screen { get { return null; } }
        public ICapriVoice Voice { get { return null; } }
        public ICapriTimers Timers { get { return null; } }
        public ICapriPersonality Personality { get { return null; } }
        public ICapriSettings Settings { get { return null; } }
        public ICapriSkins Skins { get { return null; } }
        public ICapriBody Her { get { return null; } }
        public ICapriBrain Brain { get { return null; } }
        public ICapriStore Store { get { return Data; } }
        public ICapriSystem System { get { return null; } }
        public ICapriMods Mods { get { return null; } }
        public ICapriKeys Keys { get { return null; } }
        public ICapriSounds Sounds { get { return null; } }
        public ICapriMusic Music { get { return null; } }
    }
}
