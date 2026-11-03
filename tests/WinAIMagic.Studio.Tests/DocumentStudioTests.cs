using System.Net;
using System.ComponentModel;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using WinAIMagic.Chat;
using WinAIMagic.Core.Support;
using WinAIMagic.Documents;
using WinAIMagic.Studio;
using Xunit;

namespace WinAIMagic.Studio.Tests;

public sealed class DocumentStudioTests : StudioTest
{
    private List<string> DocxInFolder => Directory.GetFiles(Folder, "*.docx").Select(f => Path.GetFileName(f)!).ToList();

    // MARK: - the way of a document

    [Fact]
    public Task TheAttachmentsDecideWhatTheDocumentConsistsOf() => UiThread.Run(async () =>
    {
        Attach("eisen.txt", "Een AQS bestaat uit een inleiding en bevindingen.");
        Answer(Plan);
        Studio.Make("Maak een AQS", true);
        await WaitUntilDone();
        Assert.Null(Studio.Problem);

        // the attachments reach the heavy model first, with the instruction
        var first = Model.Requests[0];
        Assert.Equal("heavy.model.test", first.Host);
        Assert.Contains("Een AQS bestaat uit een inleiding en bevindingen.", first.User);
        Assert.Contains("Maak een AQS", first.User);

        Assert.Equal(["Inleiding", "Bevindingen", "Beveiliging", "Bronnen"], DraftSections.List(Studio.Text).Select(s => s.Title));
        Assert.Equal("AQS Citrix.md", Path.GetFileName(Studio.Selected));
        Assert.Equal("AQS Citrix", new DocumentDraft(Studio.Text).Title);
        Assert.Contains(Studio.Steps, s => s.Text.Contains("eisen.txt"));      // the user sees where the structure came from

        // a draft on disk, and no document yet: the house style comes last
        Assert.Empty(DocxInFolder);
    });

    [Fact]
    public Task WhatTheAttachmentsSayNeedsNoInternet() => UiThread.Run(async () =>
    {
        Attach("eisen.txt", "Alles staat hierin.");
        Answer(PlanWithoutSearch, "Tekst.");
        Studio.Make("Maak een AQS", true);
        await WaitUntilDone();
        Assert.Null(Studio.Problem);
        Assert.Empty(Searched);
        Assert.DoesNotContain("# Bronnen", Studio.Text);
    });

    [Fact]
    public Task InternetCanBeLeftOut() => UiThread.Run(async () =>
    {
        Attach("eisen.txt", "Iets.");
        Answer(Plan, "Tekst.");
        Studio.Make("Maak een AQS", false);
        await WaitUntilDone();
        Assert.Empty(Searched);
        Assert.DoesNotContain("ZOEK staat op een eigen regel", Model.Requests[0].System);
    });

    [Fact]
    public Task TheClientStaysOffTheSearchEngine() => UiThread.Run(async () =>
    {
        Studio.Project = new DocumentProject([new ProjectField("Klant", "Gemeente Voorbeeld")]);
        Attach("eisen.txt", "Iets.");
        Answer(Plan);
        Studio.Make("Maak een AQS", true);
        await WaitUntilDone();
        Assert.Equal("eisen BIO aan MFA", Searched[0]);
        Assert.DoesNotContain("gemeente voorbeeld", string.Join(" ", Searched).ToLowerInvariant());
    });

    [Fact]
    public Task FastModelReadsPagesAndHeavyModelPlansAndWrites() => UiThread.Run(async () =>
    {
        Answer(Plan);
        Studio.Make("Maak een AQS", true);
        await WaitUntilDone();
        Assert.Equal("heavy.model.test", Model.Requests[0].Host);
        Assert.Contains(Model.Requests, r => r.Host == "fast.model.test");
        // text from the web reaches a model without tools, so a page cannot make it do anything
        Assert.All(Model.Requests, r => Assert.False(r.Body.ContainsKey("tools")));
    });

    [Fact]
    public Task SourcesAreCheckedAndNumberedByTheApp() => UiThread.Run(async () =>
    {
        Answer(PlanWithoutSearch + "ZOEK: mfa eisen\n", "MFA is verplicht [2]. Ook [1, 2]. Verzonnen [9]. Volgens bron 2 veel.");
        Studio.Make("Hoe zit het met MFA?", true);
        await WaitUntilDone();
        Assert.Null(Studio.Problem);
        var body = new DocumentDraft(Studio.Text).Body;
        // in the order the text uses them: B first, the made-up one gone
        Assert.Contains("verplicht [1].", body);
        Assert.Contains("[2, 1]", body);
        Assert.DoesNotContain("[9]", body);
        Assert.Contains("1. [Bron B](https://b.example/2)", body);
        Assert.Contains("2. [Bron A](https://a.example/1)", body);
        Assert.DoesNotContain("ruis.example", body);
        Assert.Contains(Studio.Notes, n => n.Contains("[9]"));
        Assert.Contains(Studio.Notes, n => n.Contains("'bron 3'"));
        Assert.Contains(Studio.Notes, n => n.Contains("2 pagina's"));
    });

    [Fact]
    public Task TheListOfSourcesIsNoReasonForAWarning() => UiThread.Run(async () =>
    {
        Answer(PlanWithoutSearch + "ZOEK: mfa\n", "Een feit [1].");
        Studio.Make("Hoe snel", true);
        await WaitUntilDone();
        Assert.DoesNotContain(Studio.Notes, n => n.Contains("'bron 3'"));
    });

    [Fact]
    public Task APageIsJudgedAgainstTheQuestionThatFoundIt() => UiThread.Run(async () =>
    {
        var asked = new List<string>();
        Model.Answer = (system, user) =>
        {
            if (system.Contains("hoe een document eruit moet zien")) return Plan;
            if (system.Contains("webpagina"))
            {
                asked.Add(user);
                return "MFA is verplicht.";
            }
            return "Tekst [1].";
        };
        Attach("eisen.txt", "Iets.");
        Studio.Make("Maak een AQS", true);
        await WaitUntilDone();
        Assert.NotEmpty(asked);
        Assert.All(asked, a => Assert.True(a.Contains("Vraag: eisen BIO aan MFA") || a.Contains("Vraag: citrix beveiliging"), a));
        Assert.DoesNotContain(asked, a => a.Contains("Maak een AQS"));
    });

    [Fact]
    public Task EveryQuestionGetsItsTurn() => UiThread.Run(async () =>
    {
        var read = new List<string>();
        Studio.SearchWeb = (q, _) => Task.FromResult(Enumerable.Range(1, 20).Select(i => new WebTools.Hit($"{q} {i}", $"https://x.example/{q}/{i}", "")).ToList());
        Studio.FetchPage = (url, _) =>
        {
            read.Add(url);
            return Task.FromResult(("Pagina", "Inhoud."));
        };
        Answer(PlanWithoutSearch + "ZOEK: een\nZOEK: twee\nZOEK: drie\n", "Tekst [1].");
        Attach("eisen.txt", "Iets.");
        Studio.Make("Maak een AQS", true);
        await WaitUntilDone();
        Assert.Equal(DocumentStudio.MaxPages, read.Count);
        Assert.Equal(3, read.Select(u => new Uri(u).Segments[1]).Distinct().Count());  // not all pages from the first question
    });

    [Fact]
    public Task NothingFoundWithoutAttachmentsWritesNothing() => UiThread.Run(async () =>
    {
        Answer(Plan);
        Studio.SearchWeb = (_, _) => Task.FromResult(new List<WebTools.Hit>());
        Studio.Make("Iets onvindbaars", true);
        await WaitUntilDone();
        Assert.NotNull(Studio.Problem);
        Assert.Empty(Directory.GetFiles(Folder, "*.md"));
    });

    [Fact]
    public Task TheTextOnlyChangesOnTheUiThread() => UiThread.Run(async () =>
    {
        int offUi = 0, changes = 0;
        PropertyChangedEventHandler watch = (_, _) =>
        {
            changes++;
            if (Environment.CurrentManagedThreadId != UiThread.Id) offUi++;
        };
        Studio.PropertyChanged += watch;
        Answer(Plan);
        Attach("eisen.txt", "Iets.");
        Studio.Make("Maak een AQS", true);
        await WaitUntilDone();
        Studio.PropertyChanged -= watch;
        Assert.True(changes > 5, $"{changes}");
        Assert.Equal(0, offUi);     // a screen changed from another thread locked up the whole app in 1.9.3
    });

    [Fact]
    public Task TheScreenKeepsWorkingWhileTheModelWrites() => UiThread.Run(async () =>
    {
        Studio.SearchWeb = (_, _) => Task.FromResult(new List<WebTools.Hit>());
        var chunk = string.Concat(Enumerable.Repeat("Een zin met woorden. ", 400));
        Model.Answer = (system, _) =>
        {
            Thread.Sleep(50);       // a model takes its time, off this thread
            return system.Contains("hoe een document eruit moet zien") ? Plan : chunk;
        };
        Studio.Make("Maak een AQS", false);
        int ticks = 0;
        var start = DateTime.UtcNow;
        while (Studio.Busy && DateTime.UtcNow - start < TimeSpan.FromSeconds(30))
        {
            ticks++;
            await Task.Delay(1);
        }
        Assert.False(Studio.Busy);
        Assert.True(ticks > 1, $"{ticks}");
        Assert.Contains("Een zin met woorden.", Studio.Text);
    });

    [Fact]
    public Task NothingFoundWithAttachmentsStillWritesAndSaysSo() => UiThread.Run(async () =>
    {
        Attach("eisen.txt", "Iets.");
        Answer(Plan, "Tekst.");
        Studio.SearchWeb = (_, _) => Task.FromResult(new List<WebTools.Hit>());
        Studio.Make("Maak een AQS", true);
        await WaitUntilDone();
        Assert.Null(Studio.Problem);
        Assert.NotEmpty(DraftSections.List(Studio.Text));
        Assert.Contains(Studio.Notes, n => n.Contains("niets gevonden"));
    });

    [Fact]
    public Task WithoutAttachmentsOrSourcesTheUserIsWarned() => UiThread.Run(async () =>
    {
        Answer(PlanWithoutSearch, "Tekst.");
        Studio.Make("Maak een AQS", false);
        await WaitUntilDone();
        Assert.Contains(Studio.Notes, n => n.Contains("zonder bijlagen"));
    });

    [Fact]
    public Task AnEngineThatFailsIsNotCalledAnUnreadablePage() => UiThread.Run(async () =>
    {
        Answer(Plan);
        int calls = 0;
        Studio.Endpoint = (_, _) =>
        {
            calls++;
            return Task.FromResult(calls > 1 ? (new Uri("http://127.0.0.1:1"), "weg") : (new Uri("http://heavy.model.test"), "zwaar"));
        };
        Attach("eisen.txt", "Iets.");
        Studio.Make("Maak een AQS", true);
        await WaitUntilDone();
        Assert.NotNull(Studio.Problem);
    });

    [Fact]
    public Task AModelThatKeepsToNoFormGetsTheHouseStylesHeadings() => UiThread.Run(async () =>
    {
        Studio.UseHouseStyle(Fixture);
        Model.Answer = (system, _) => system.Contains("hoe een document eruit moet zien") ? "Dat weet ik niet." : "Tekst.";
        Studio.Make("Maak een AQS", false);
        await WaitUntilDone();
        Assert.Null(Studio.Problem);
        Assert.Contains("Inleiding", DraftSections.List(Studio.Text).Select(s => s.Title));
    });

    [Fact]
    public Task ThinkingOffIsAlsoAskedOfTemplatesThatIgnoreIt() => UiThread.Run(async () =>
    {
        Answer(PlanWithoutSearch, "Tekst.");
        Attach("eisen.txt", "Iets.");
        Studio.Make("Maak een AQS", false);
        await WaitUntilDone();
        var writing = Model.Requests.Skip(1).ToList();
        Assert.NotEmpty(writing);
        Assert.All(writing, r =>
        {
            Assert.Equal(0, r.Body["thinking_budget_tokens"]!.GetValue<int>());  // GLM always opens with <think>
            Assert.False(r.Body["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>());
        });
        Assert.True(Model.Requests[0].Body["max_tokens"]!.GetValue<int>() > 6000);   // a long structure must fit, next to the thinking
    });

    /// Tokens, temperature and thinking per step, as they stand in the Swift.
    [Fact]
    public Task EachStepAsksWhatItShould() => UiThread.Run(async () =>
    {
        Studio.ContextLimit = () => 32_768;
        Studio.Project = new DocumentProject([new ProjectField("Versie", "")]);
        Model.Answer = (system, user) =>
        {
            if (system.Contains("hoe een document eruit moet zien")) return PlanWithoutSearch + "ZOEK: mfa\n";
            if (system.Contains("webpagina")) return user.Contains("appeltaart") ? "NIETS" : "MFA is verplicht.";
            if (system.Contains("voorblad")) return "";
            if (user.Contains("sectie 'Bevindingen'") && user.Contains("Lege poging")) return "";
            return "Tekst [1].";
        };
        Attach("eisen.txt", "Iets.");
        Studio.Make("Maak een AQS", true);
        await WaitUntilDone();
        Assert.Null(Studio.Problem);

        static (int MaxTokens, double Temperature, int Thinking, bool Enabled) Asked(FakeModel.Request r) =>
            (r.Body["max_tokens"]!.GetValue<int>(), r.Body["temperature"]!.GetValue<double>(), r.Body["thinking_budget_tokens"]!.GetValue<int>(),
             r.Body["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>());

        Assert.Equal((7000, 0.2, 1000, true), Asked(Model.Requests.Single(r => r.System.Contains("hoe een document eruit moet zien"))));
        Assert.Equal((Math.Max(600, 32_768 / 16) / 3, 0.1, 0, false), Asked(Model.Requests.First(r => r.System.Contains("webpagina"))));
        Assert.Equal((3000, 0.5, 0, false), Asked(Model.Requests.First(r => r.User.Contains("sectie 'Inleiding'"))));
        Assert.Equal((300, 0.1, 0, false), Asked(Model.Requests.Single(r => r.System.Contains("voorblad"))));
        Assert.Contains("nagedacht", Model.Requests[0].Body["reasoning_budget_message"]!.GetValue<string>());
        Assert.False(Model.Requests[1].Body.ContainsKey("reasoning_budget_message"));
    });

    [Fact]
    public Task ASectionWithSubsectionsGetsLessRoomAndARetryGetsThinking() => UiThread.Run(async () =>
    {
        Model.Answer = (system, user) =>
        {
            if (system.Contains("hoe een document eruit moet zien")) return "TITEL: T\n# Een\n> X.\n## Sub\n> Y.\n# Twee\n> Z.\n";
            if (system.Contains("voorblad")) return "";
            return user.Contains("sectie 'Sub'") ? "" : "Tekst.";
        };
        Studio.Make("Maak iets", false);
        await WaitUntilDone();
        Assert.Equal(800, Model.Requests.First(r => r.User.Contains("sectie 'Een'")).Body["max_tokens"]!.GetValue<int>());
        var sub = Model.Requests.Where(r => r.User.Contains("sectie 'Sub'")).ToList();
        Assert.Equal(2, sub.Count);
        Assert.Equal(0, sub[0].Body["thinking_budget_tokens"]!.GetValue<int>());
        Assert.Equal(1500, sub[1].Body["thinking_budget_tokens"]!.GetValue<int>());
        Assert.Equal(4500, sub[1].Body["max_tokens"]!.GetValue<int>());
    });

    [Fact]
    public Task LongAttachmentsGoThroughTheFastModelFirst() => UiThread.Run(async () =>
    {
        Studio.ContextLimit = () => 100;    // 150 characters
        Model.Answer = (system, _) =>
        {
            if (system.Contains("volledige, feitelijke notities")) return "Een notitie.";
            if (system.Contains("hoe een document eruit moet zien")) return PlanWithoutSearch;
            return "Tekst.";
        };
        Attach("lang.txt", new string('x', 400));
        Studio.Make("Maak een AQS", false);
        await WaitUntilDone();
        Assert.Null(Studio.Problem);
        var notes = Model.Requests.Where(r => r.System.Contains("volledige, feitelijke notities")).ToList();
        Assert.True(notes.Count > 1);
        Assert.All(notes, r =>
        {
            Assert.Equal("fast.model.test", r.Host);
            Assert.Equal(1200, r.Body["max_tokens"]!.GetValue<int>());
            Assert.Equal(0.2, r.Body["temperature"]!.GetValue<double>());
        });
        Assert.Contains("Notities uit de bijlagen:", Model.Requests.First(r => r.Host == "heavy.model.test").User);
        Assert.DoesNotContain(new string('x', 400), Model.Requests.First(r => r.Host == "heavy.model.test").User);
    });

    [Fact]
    public Task OneSilentSectionDoesNotStopTheOthers() => UiThread.Run(async () =>
    {
        int calls = 0;
        Model.Answer = (system, user) =>
        {
            if (system.Contains("hoe een document eruit moet zien")) return Plan;
            if (system.Contains("voorblad")) return "";
            if (user.Contains("sectie 'Bevindingen'"))
            {
                calls++;
                return "";
            }
            return "Tekst.";
        };
        Attach("eisen.txt", "Iets.");
        Studio.Make("Maak een AQS", false);
        await WaitUntilDone();
        Assert.Null(Studio.Problem);
        Assert.Equal(2, calls);     // once more before giving up
        Assert.Contains(Studio.Notes, n => n.Contains("Bevindingen"));
        Assert.Contains("Per thema.", Studio.Text);     // its instruction stays
        var sections = DraftSections.List(Studio.Text);
        Assert.Equal(["Inleiding", "Bevindingen", "Beveiliging"], sections.Select(s => s.Title));
        Assert.Contains("Tekst.", Studio.Text.Substring(sections[2].Range.Location, sections[2].Range.Length));     // the section after it is written
    });

    [Fact]
    public Task ASilentRetryCanStillAnswer() => UiThread.Run(async () =>
    {
        int calls = 0;
        Model.Answer = (system, _) =>
        {
            if (system.Contains("hoe een document eruit moet zien")) return PlanWithoutSearch;
            if (system.Contains("voorblad")) return "";
            calls++;
            return calls == 1 ? "" : "Tweede poging.";
        };
        Attach("eisen.txt", "Iets.");
        Studio.Make("Maak een AQS", false);
        await WaitUntilDone();
        Assert.Contains("Tweede poging.", Studio.Text);
        Assert.Empty(Studio.Notes);
    });

    [Fact]
    public Task WritingKeepsThePlannedSections() => UiThread.Run(async () =>
    {
        Model.Answer = (system, _) =>
        {
            if (system.Contains("hoe een document eruit moet zien")) return Plan;
            if (system.Contains("voorblad")) return "";
            // a model that adds a heading must not shift the sections
            return "### Extra kop\n\nTekst voor de sectie.";
        };
        Attach("eisen.txt", "Iets.");
        Studio.Make("Maak een AQS", false);
        await WaitUntilDone();
        Assert.Equal(["Inleiding", "Bevindingen", "Beveiliging"], DraftSections.List(Studio.Text).Select(s => s.Title));
        Assert.DoesNotContain("<!-- ", Studio.Text);    // instructions are replaced by text
        Assert.Contains("**Extra kop**", Studio.Text);
        Assert.Contains(Model.Requests, r => r.User.Contains("Per thema."));    // each section gets its instruction
    });

    [Fact]
    public Task SummariesGoLastAndTheDigestReachesThem() => UiThread.Run(async () =>
    {
        Model.Answer = (system, _) =>
        {
            if (system.Contains("hoe een document eruit moet zien")) return "TITEL: T\n# Samenvatting\n# Een\n# Twee\n";
            if (system.Contains("voorblad")) return "";
            return "Woorden van een sectie.";
        };
        Studio.Make("Maak iets", false);
        await WaitUntilDone();
        var written = Model.Requests.Where(r => r.User.Contains("Schrijf nu de inhoud")).ToList();
        Assert.Contains("sectie 'Een'", written[0].User);
        Assert.Contains("sectie 'Twee'", written[1].User);
        Assert.Contains("sectie 'Samenvatting'", written[2].User);
        Assert.Contains("- Een: Woorden van een sectie.", written[2].User);
    });

    [Fact]
    public Task StopKeepsWhatIsThere() => UiThread.Run(async () =>
    {
        int written = 0;
        Model.Answer = (system, _) =>
        {
            if (system.Contains("hoe een document eruit moet zien")) return "TITEL: T\n# Een\n# Twee\n# Drie\n";
            if (system.Contains("voorblad")) return "";
            if (++written == 1) return "Eerste sectie.";
            Studio.Stop();
            return "Tweede sectie.";
        };
        Studio.Make("Maak iets", false);
        await WaitUntilDone();
        Assert.Null(Studio.Problem);
        Assert.Contains(Studio.Steps, s => s.State == StepState.Failed);
        Assert.DoesNotContain(Studio.Steps, s => s.State == StepState.Running);
        Assert.Contains("Eerste sectie.", File.ReadAllText(Studio.Selected!));
    });

    // MARK: - the house style

    [Fact]
    public void HouseStyleKeepsItsOwnName()
    {
        Studio.UseHouseStyle(Named("aqs template.docx"));
        Assert.Equal("aqs template.docx", Path.GetFileName(Studio.HouseStyle));
        Assert.Equal("aqs template.docx", Path.GetFileName(NewStudio(Folder).HouseStyle));

        var bad = Path.Combine(Folder, "geen-word.docx");
        File.WriteAllText(bad, "geen zip");
        Studio.UseHouseStyle(bad);
        Assert.NotNull(Studio.Problem);
        Assert.Equal("aqs template.docx", Path.GetFileName(Studio.HouseStyle));     // a bad file does not replace the house style

        Studio.UseHouseStyle(Named("andere.docx"));
        Assert.Equal("andere.docx", Path.GetFileName(Studio.HouseStyle));   // a project has one house style
    }

    [Fact]
    public void TheOldHouseStyleFileStillCounts()
    {
        File.Copy(Fixture, Path.Combine(Folder, "huisstijl.docx"));
        Studio.Refresh();
        Assert.Equal("huisstijl.docx", Path.GetFileName(Studio.HouseStyle));
    }

    [Fact]
    public Task TheLayoutComesLastAndNotItsText() => UiThread.Run(async () =>
    {
        Studio.UseHouseStyle(Fixture);
        Attach("eisen.txt", "Iets.");
        Answer(PlanWithoutSearch, "Onze eigen tekst.");
        Studio.Make("Maak een AQS", false);
        await WaitUntilDone();
        Studio.Deliver();
        Assert.Empty(Studio.Report!.Problems);
        var model = new DocModel(new DocxPackage(Studio.Report.Url!));
        Assert.Equal(["Inleiding", "Bevindingen"], model.Outline().Select(n => n.Title));   // the structure of the draft, not of the house style
        var text = string.Join("\n", model.Blocks.Select(b => b.Text));
        Assert.Contains("Onze eigen tekst.", text);
        Assert.DoesNotContain("Voorbeeldknelpunt", text);   // the house style's example text is gone
        Assert.Contains("Architecture Quick Scan", text);   // its cover stays
    });

    [Fact]
    public void DeliverKeepsTheOldDocument()
    {
        Studio.NewDocument("Rapport");
        Studio.Deliver();
        var first = Studio.Report!.Url!;
        Assert.Equal("Rapport.docx", Path.GetFileName(first));
        Assert.Equal(Folder, Path.GetDirectoryName(first));
        Studio.Deliver();
        Assert.Equal("Rapport 2.docx", Path.GetFileName(Studio.Report!.Url));
        Assert.True(File.Exists(first));
    }

    // MARK: - the project

    [Fact]
    public Task WhatTheProjectKnowsIsEnteredOnce() => UiThread.Run(async () =>
    {
        Studio.UseHouseStyle(Fixture);
        var keys = Studio.Project.Fields.Select(f => DocxFields.Normalized(f.Key)).ToHashSet();
        Assert.Superset(new HashSet<string> { "klant", "versie" }, keys);   // the cover's fields become the project's
        Studio.Project.Fields.First(f => DocxFields.Normalized(f.Key) == "klant").Value = "RIVM";
        Attach("eisen.txt", "Versie 0.2 van de scan.");

        var prompts = new List<string>();
        Model.Answer = (system, user) =>
        {
            prompts.Add(user);
            if (system.Contains("hoe een document eruit moet zien")) return PlanWithoutSearch;
            if (system.Contains("voorblad")) return "versie: 0.2";
            return "Tekst.";
        };
        Studio.Make("Maak een AQS", false);
        await WaitUntilDone();
        Assert.Null(Studio.Problem);
        Assert.Contains(prompts, p => p.Contains("Klant: RIVM") || p.Contains("klant: RIVM"));
        string? Field(string key) => new DocumentDraft(Studio.Text).Fields.Where(f => DocxFields.Normalized(f.Key) == key).Select(f => f.Value).FirstOrDefault();
        Assert.Equal("RIVM", Field("klant"));
        Assert.Equal("0.2", Field("versie"));
        Assert.False(string.IsNullOrEmpty(Field("datum")));     // today's date needs nobody

        // what was found out is known for the next document, without a model
        Studio.NewDocument("Tweede");
        Assert.Equal("RIVM", Field("klant"));
        Assert.Equal("0.2", Field("versie"));

        // and it is all still there when the project is opened again
        var again = NewStudio(Folder);
        Assert.Equal("RIVM", again.Project.Value("klant"));
        Assert.Equal("sjabloon.docx", Path.GetFileName(again.HouseStyle));
        Assert.Equal(["eisen.txt"], again.Attachments.Select(a => Path.GetFileName(a)));
        Assert.Equal(["AQS Citrix.md", "Tweede.md"], again.Drafts.Select(d => Path.GetFileName(d)!).Order());
    });

    [Fact]
    public void ExampleValuesOfTheHouseStyleStayOut()
    {
        Studio.UseHouseStyle(Fixture);
        Studio.NewDocument("Een");
        var fields = new DocumentDraft(Studio.Text).Fields;
        Assert.Equal("Een", fields.First(f => f.Key == "titel").Value);
        Assert.Equal("", fields.First(f => DocxFields.Normalized(f.Key) == "klant").Value);     // [Klantnaam] is no value
    }

    [Fact]
    public void ProjectsAreKept()
    {
        var root = Path.Combine(Folder, "wortel");
        var s = NewStudio(Folder, root);
        s.CreateProject("Citrix SPA");
        s.CreateProject("Citrix SPA");
        Assert.Equal("Citrix SPA 2", Path.GetFileName(s.Folder));   // an existing project is never taken over
        s.Project = new DocumentProject([new ProjectField("Klant", "Tweede")]);
        s.Choose(Path.Combine(root, "Citrix SPA"));
        Assert.Empty(s.Project.Fields);     // each project its own
        Assert.Equal(["Citrix SPA", "Citrix SPA 2"], NewStudio(null, root).Projects.Select(p => Path.GetFileName(p)!).Where(n => n.StartsWith("Citrix")));
    }

    [Fact]
    public void ProjectFileIsTheMacsFormat()
    {
        var macFile = "{\"fields\":[{\"value\":\"RIVM\",\"id\":\"E621E1F8-C36C-495A-93FC-0C247A3E6E5F\",\"key\":\"Klant\"}]}";
        File.WriteAllText(Path.Combine(Folder, "project.json"), macFile);
        var s = NewStudio(Folder);
        Assert.Equal("RIVM", s.Project.Value("klant"));
        s.Project.Fields[0].Value = "Ander";
        var saved = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(Folder, "project.json")))!;
        var field = saved["fields"]![0]!;
        Assert.Equal("E621E1F8-C36C-495A-93FC-0C247A3E6E5F", field["id"]!.GetValue<string>());     // as Swift writes a UUID
        Assert.Equal("Klant", field["key"]!.GetValue<string>());
        Assert.Equal("Ander", field["value"]!.GetValue<string>());
    }

    [Fact]
    public void AttachmentWithoutTextIsRefused()
    {
        Attach("leeg.txt", "   ");
        Assert.NotNull(Studio.Problem);
        Assert.Empty(Studio.Attachments);
    }

    [Fact]
    public Task AWordAttachmentIsReadWhole() => UiThread.Run(async () =>
    {
        Studio.AddAttachment(Fixture);
        var plan = "";
        Model.Answer = (system, user) =>
        {
            if (system.Contains("hoe een document eruit moet zien"))
            {
                plan = user;
                return PlanWithoutSearch;
            }
            return "Tekst.";
        };
        Studio.Make("Maak een AQS", false);
        await WaitUntilDone();
        Assert.Contains("Architecture Quick Scan", plan);   // also what stands before the first heading
        Assert.Contains("Beschrijf de aanleiding", plan);   // and the instructions
    });

    private string Pdf(string name, string? text)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
        if (text != null) page.AddText(text, 12, new UglyToad.PdfPig.Core.PdfPoint(50, 700), font);
        var path = Path.Combine(Root, name);
        File.WriteAllBytes(path, builder.Build());
        return path;
    }

    [Fact]
    public Task APdfGivesItsEmbeddedText() => UiThread.Run(async () =>
    {
        Studio.AddAttachment(Pdf("eisen.pdf", "Een AQS bestaat uit twee delen."));
        Assert.Null(Studio.Problem);
        Assert.Equal(["eisen.pdf"], Studio.Attachments.Select(a => Path.GetFileName(a)));
        Answer(PlanWithoutSearch, "Tekst.");
        Studio.Make("Maak een AQS", false);
        await WaitUntilDone();
        Assert.Contains("Een AQS bestaat uit twee delen.", Model.Requests[0].User);
    });

    [Fact]
    public void AScanIsRefused()
    {
        Studio.AddAttachment(Pdf("scan.pdf", null));
        Assert.Contains("scan.pdf", Studio.Problem);
        Assert.Contains("geen tekst", Studio.Problem);
        Assert.Empty(Studio.Attachments);
    }

    [Fact]
    public void AFileThatIsNoPdfIsRefused()
    {
        var path = Path.Combine(Root, "kapot.pdf");
        File.WriteAllText(path, "geen pdf");
        Studio.AddAttachment(path);
        Assert.Equal("Kan kapot.pdf niet lezen.", Studio.Problem);
    }

    [Fact]
    public void HiddenFilesAreNoAttachments()
    {
        var dir = Path.Combine(Folder, "bijlagen");
        Directory.CreateDirectory(dir);
        foreach (var name in new[] { "a.txt", ".verborgen", "~$open.docx", "desktop.ini" }) File.WriteAllText(Path.Combine(dir, name), "x");
        File.SetAttributes(Path.Combine(dir, "desktop.ini"), FileAttributes.Hidden | FileAttributes.System);
        Studio.Refresh();
        Assert.Equal(["a.txt"], Studio.Attachments.Select(a => Path.GetFileName(a)));
    }

    // MARK: - changing a draft

    [Fact]
    public Task ActionWorksOnTheSectionAtTheCursorAndCanBeUndone() => UiThread.Run(async () =>
    {
        Studio.NewDocument("Tekst");
        Studio.Text = "# Een\n\nEerste tekst.\n\n# Twee\n\nTweede tekst.\n";
        var before = Studio.Text;
        Studio.Selection = new NSRange(before.IndexOf("Tweede", StringComparison.Ordinal), 0);
        Assert.Equal("sectie 'Twee'", Studio.ScopeTitle);
        Model.Answer = (_, _) => "# Twee\n\nBetere tekst.";
        Studio.Apply(QuickAction.Improve);
        await WaitUntilDone();
        Assert.Equal("# Een\n\nEerste tekst.\n\n# Twee\n\nBetere tekst.\n", Studio.Text);
        Assert.Equal("fast.model.test", Model.Requests[^1].Host);
        Assert.Equal(0, Model.Requests[^1].Body["thinking_budget_tokens"]!.GetValue<int>());
        Assert.Equal(1024, Model.Requests[^1].Body["max_tokens"]!.GetValue<int>());
        Assert.Equal(0.3, Model.Requests[^1].Body["temperature"]!.GetValue<double>());
        Studio.Undo();
        Assert.Equal(before, Studio.Text);
        Assert.Null(Studio.Previous);   // one level
        Assert.Equal(before, File.ReadAllText(Studio.Selected!));
    });

    [Fact]
    public Task OwnInstructionGoesToTheHeavyModelOnTheSelection() => UiThread.Run(async () =>
    {
        Studio.NewDocument("Tekst");
        Studio.Text = "Een zin die blijft. Een zin die verandert.";
        Studio.Selection = new NSRange(Studio.Text.IndexOf("Een zin die verandert.", StringComparison.Ordinal), "Een zin die verandert.".Length);
        Assert.Equal("de selectie", Studio.ScopeTitle);
        Model.Answer = (_, _) => "Nieuw.";
        Studio.Apply("Herschrijf");
        await WaitUntilDone();
        Assert.Equal("Een zin die blijft. Nieuw.", Studio.Text);
        Assert.Equal("heavy.model.test", Model.Requests[^1].Host);
        Assert.Equal(1024 + 1500, Model.Requests[^1].Body["max_tokens"]!.GetValue<int>());
        Assert.Equal(1500, Model.Requests[^1].Body["thinking_budget_tokens"]!.GetValue<int>());
    });

    [Fact]
    public void WithoutASectionOrSelectionTheWholeDocumentIsTheScope()
    {
        Studio.NewDocument("Tekst");
        Studio.Text = "Alleen tekst.\n";
        Studio.Selection = new NSRange(0, 0);
        Assert.Equal("het hele document", Studio.ScopeTitle);
    }

    [Fact]
    public Task EmptyAnswerChangesNothing() => UiThread.Run(async () =>
    {
        Studio.NewDocument("Tekst");
        Studio.Text = "# Een\n\nTekst.\n";
        Model.Answer = (_, _) => "";
        Studio.Apply(QuickAction.Shorter);
        await WaitUntilDone();
        Assert.Equal("# Een\n\nTekst.\n", Studio.Text);
        Assert.NotNull(Studio.Problem);
    });

    [Fact]
    public Task TheTextIsSavedAfter600Milliseconds() => UiThread.Run(async () =>
    {
        Studio.NewDocument("Tekst");
        var file = Studio.Selected!;
        var start = File.ReadAllText(file);
        Studio.Text = "Eerste.";
        await Task.Delay(300);
        Studio.Text = "Tweede.";    // it starts over
        await Task.Delay(400);
        Assert.Equal(start, File.ReadAllText(file));
        await Task.Delay(500);
        Assert.Equal("Tweede.", File.ReadAllText(file));
    });

    [Fact]
    public void TheTextIsKeptWithPlainNewlines()
    {
        Studio.NewDocument("Tekst");
        Studio.Text = "---\rtitel: x\r---\r\r# Een\r\nTekst";   // what a WinUI TextBox gives
        Assert.Equal("---\ntitel: x\n---\n\n# Een\nTekst", Studio.Text);
        Assert.Equal("x", new DocumentDraft(Studio.Text).Fields[0].Value);
    }

    [Fact]
    public void ADraftWithAByteOrderMarkKeepsItsFields()
    {
        var file = Path.Combine(Folder, "Notepad.md");
        File.WriteAllText(file, "---\r\ntitel: x\r\n---\r\n\r\n# Een\r\n", new System.Text.UTF8Encoding(true));
        Studio.Open(file);
        Assert.Equal("x", new DocumentDraft(Studio.Text).Fields[0].Value);
        Assert.Equal(["Een"], DraftSections.List(Studio.Text).Select(s => s.Title));
    }

    [Fact]
    public void ADraftThatCannotBeReadIsNotOverwritten()
    {
        var file = Path.Combine(Folder, "Latin.md");
        byte[] bytes = [0x23, 0x20, 0xE9, 0xE9, 0x0A];
        File.WriteAllBytes(file, bytes);
        Studio.Open(file);
        Assert.NotNull(Studio.Problem);
        Assert.Null(Studio.Selected);
        Studio.Save();
        Assert.Equal(bytes, File.ReadAllBytes(file));
    }

    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGNgYPjPAAAEAgEAVgP1AgAAAABJRU5ErkJggg==");

    [Fact]
    public void AnImageFromADraftStaysInsideTheProject()
    {
        var outside = Path.Combine(Root, "geheim.png");
        File.WriteAllBytes(outside, Png);
        File.WriteAllBytes(Path.Combine(Folder, "eigen.png"), Png);
        Studio.NewDocument("Plaatjes");
        Studio.Text = $"# Kop\n\n![](../geheim.png)\n\n![]({outside})\n\n![](eigen.png)\n";
        Studio.Deliver();
        var pkg = new DocxPackage(Studio.Report!.Url!);
        Assert.Single(pkg.Names, n => n.StartsWith("word/media/", StringComparison.Ordinal));
    }

    [Fact]
    public void ALinkOutOfTheProjectDoesNotBringAnImageIn()
    {
        var outside = Path.Combine(Root, "buiten");
        Directory.CreateDirectory(outside);
        File.WriteAllBytes(Path.Combine(outside, "geheim.png"), Png);
        var link = Path.Combine(Folder, "koppeling");
        try { Directory.CreateSymbolicLink(link, outside); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }     // no right to make one
        Studio.NewDocument("Plaatjes");
        Studio.Text = "# Kop\n\n![](koppeling/geheim.png)\n";
        Studio.Deliver();
        Assert.DoesNotContain(new DocxPackage(Studio.Report!.Url!).Names, n => n.StartsWith("word/media/", StringComparison.Ordinal));
    }

    [Fact]
    public void NewFileNeverOverwrites()
    {
        File.WriteAllBytes(Path.Combine(Folder, "Rapport.docx"), []);
        Assert.Equal("Rapport 2.docx", Path.GetFileName(DocumentStudio.Unique(Folder, "Rapport", "docx")));
        Assert.Equal("Wat is  dit", DocumentStudio.FileName("Wat/is: dit?"));
        Assert.Equal("_NUL", DocumentStudio.FileName("NUL"));
    }

    [Fact]
    public void DeletingGoesThroughTheRecycleBinHook()
    {
        var trashed = new List<string>();
        Studio.Trash = trashed.Add;
        Studio.NewDocument("Weg");
        var file = Studio.Selected!;
        Studio.Delete(file);
        Assert.Equal([file], trashed);
        Assert.Null(Studio.Selected);
        Assert.Equal("", Studio.Text);
        Assert.Equal(StudioPage.Project, Studio.Page);
    }

    [Fact]
    public void TheRecycleBinTakesAFile()
    {
        var file = Path.Combine(Root, "wam-test-prullenbak.txt");
        File.WriteAllText(file, "x");
        new DocumentStudio(Folder).Trash(file);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void TheWordEnginesTextsFollowTheLanguage()
    {
        _ = Studio;     // the studio plugs the language in
        Assert.Equal("[Afbeelding niet gevonden: a.png]", WinAIMagic.Documents.Loc.L("[Afbeelding niet gevonden: %@]", "a.png"));
        Settings.Shared.Language = "en";
        Assert.NotEqual("[Afbeelding niet gevonden: a.png]", WinAIMagic.Documents.Loc.L("[Afbeelding niet gevonden: %@]", "a.png"));
        Assert.Contains("a.png", WinAIMagic.Documents.Loc.L("[Afbeelding niet gevonden: %@]", "a.png"));
    }

    [Fact]
    public void TheDateIsWithoutItsWeekday()
    {
        var date = new DateTime(2026, 11, 3);
        Assert.Equal("3 november 2026", DocumentStudio.FormatLongDate(date, AppLanguage.Nl));
        Assert.Equal("November 3, 2026", DocumentStudio.FormatLongDate(date, AppLanguage.En));
        Assert.Equal("3. November 2026", DocumentStudio.FormatLongDate(date, AppLanguage.De));
        Assert.Equal("3 novembre 2026", DocumentStudio.FormatLongDate(date, AppLanguage.Fr));
    }
}

/// The plan, the sources and the search questions, without a model.
public sealed class DocumentPartsTests : StudioTest
{
    [Fact]
    public void PlanFromAModelsAnswer()
    {
        var plan = new DocumentPlan("```\nTITEL: AQS Citrix\nOpbouw: eisen.docx\nHier is de opbouw:\n# **Inleiding**\n> Waarom.\n> En hoe.\n## Scope\nZOEK: eisen BIO\nzoek: - MFA regels\n```", "Maak iets");
        Assert.Equal("AQS Citrix", plan.Title);
        Assert.Equal("eisen.docx", plan.Origin);
        Assert.Equal([new DocumentPlan.Section(1, "Inleiding", "Waarom. En hoe."), new DocumentPlan.Section(2, "Scope", "")], plan.Sections);
        Assert.Equal(["eisen BIO", "MFA regels"], plan.Searches);
        Assert.Contains("# Inleiding\n\n<!-- instructie: Waarom. En hoe. -->", plan.Outline);
        Assert.Equal("Maak iets", new DocumentPlan("onzin", "Maak iets").Title);
        Assert.Equal(5, new DocumentPlan(string.Join("\n", Enumerable.Range(1, 9).Select(i => $"ZOEK: q{i}")), "x").Searches.Count);
    }

    [Fact]
    public void TheTopHeadingIsTheTitleNotAChapter()
    {
        var plan = new DocumentPlan("OPBOUW: t.md\n# Architectuur Quick Scan\n## 1 Inleiding\n> Doel.\n### 1.1 Scope\n## 2 Advies\n### 4.2\n", "Maak een AQS");
        Assert.Equal("Architectuur Quick Scan", plan.Title);
        Assert.Equal(["1 Inleiding", "1.1 Scope", "2 Advies"], plan.Sections.Select(s => s.Title));     // a heading of only a number is no heading
        Assert.Equal([1, 2, 1], plan.Sections.Select(s => s.Level));
        // several chapters at the top stay chapters
        Assert.Equal(["Een", "Sub", "Twee"], new DocumentPlan("# Een\n## Sub\n# Twee\n", "x").Sections.Select(s => s.Title));
        // with an instruction and a few chapters below it, it is the title too
        Assert.Equal(["A", "B", "C"], new DocumentPlan("TITEL: T\n# Titel\n> Uitleg.\n## A\n## B\n## C\n", "x").Sections.Select(s => s.Title));
        // one section stays a section
        Assert.Equal(["Samenvatting"], new DocumentPlan("# Samenvatting\n", "x").Sections.Select(s => s.Title));
        Assert.Equal(["Een", "Twee"], new DocumentPlan("TITEL: T\n# Een\n# Twee\n# Half", "x", cutOff: true).Sections.Select(s => s.Title));
    }

    [Fact]
    public void CitationsAreNumberedByTheText()
    {
        Citations.Source[] sources = [new("A", "https://a"), new("B", "https://b"), new("C [x]", "https://c")];
        var done = Citations.Finish("Eerst [3], dan [1, 3] en [7]. Geen [link](https://x) of [tekst].", sources, "Bronnen", "vandaag");
        Assert.StartsWith("Eerst [1], dan [2, 1] en. Geen [link](https://x) of [tekst].", done.Markdown);
        Assert.Contains("1. [C [x)](https://c) (vandaag)\n2. [A](https://a) (vandaag)", done.Markdown);
        Assert.DoesNotContain("https://b", done.Markdown);
        Assert.Equal([7], done.Unknown);
        Assert.Equal(2, done.Cited);
        // without references the list says what was consulted
        var none = Citations.Finish("Tekst.", sources, "Bronnen", "vandaag");
        Assert.Equal(0, none.Cited);
        Assert.Contains("https://b", none.Markdown);
    }

    [Fact]
    public void SearchQuestionLosesPersonalData()
    {
        Assert.Equal("schade bsn", SearchQuery.Clean("schade NL91ABNA0417164300 piet@voorbeeld.nl bsn 123456782"));
        Assert.Equal("offerte van dossier", SearchQuery.Clean("offerte € 12.500 van 06-12345678 dossier 2024001234"));
        Assert.Equal("artikel 5.1 AVG 2024", SearchQuery.Clean("artikel 5.1 AVG 2024"));
        Assert.Equal("eisen voor over MFA", SearchQuery.Clean("eisen voor gemeente voorbeeld over MFA", ["Gemeente Voorbeeld"]));
        Assert.Equal("kosten per jaar", SearchQuery.Clean("kosten 3 miljoen per jaar"));
        Assert.Equal("bellen", SearchQuery.Clean("bellen +31 6 12345678"));
    }

    [Fact]
    public void SectionsOfADraft()
    {
        const string text = "---\nveld: x\n---\n\nVoorwoord\n\n# Een\n\nTekst\n\n```\n# geen kop\n```\n\n## Sub\n\nMeer\n\n# Twee\n";
        var list = DraftSections.List(text);
        Assert.Equal(["Een", "Sub", "Twee"], list.Select(s => s.Title));
        Assert.True(list[0].HasChildren);
        Assert.False(list[1].HasChildren);
        string Scope(int at)
        {
            var r = DraftSections.Scope(text, new NSRange(at, 0));
            return text.Substring(r.Location, r.Length);
        }
        Assert.Equal("## Sub\n\nMeer\n\n", Scope(text.IndexOf("Meer", StringComparison.Ordinal)));
        Assert.Equal("Voorwoord\n\n# Een\n\nTekst\n\n```\n# geen kop\n```\n\n## Sub\n\nMeer\n\n# Twee\n", Scope(0));
        Assert.Equal("Tekst", DraftSections.Cleaned("```markdown\n# Een\n\nTekst\n```", "Een"));
    }

    [Fact]
    public void DraftFieldsSurviveEditing()
    {
        var d = new DocumentDraft([("Betreft", "A: B # C"), ("Klant", "")], "# Kop\n");
        var again = new DocumentDraft(d.Markdown);
        Assert.Equal(["A: B # C", ""], again.Fields.Select(f => f.Value));
        Assert.Equal("# Kop\n", again.Body);
        Assert.Equal("A: B # C", again.Title);
        Assert.Empty(new DocumentDraft("Geen velden\n---\nmeer").Fields);
    }

    /// §4: text from a stream never goes to the screen synchronously, and at most every 0.1 s.
    [Fact]
    public Task TheAnswerReachesTheCallersThreadAtMostTenTimesASecond() => UiThread.Run(async () =>
    {
        var chunks = string.Concat(Enumerable.Range(0, 4000).Select(i => $"data: {{\"choices\":[{{\"delta\":{{\"content\":\"w{i} \"}}}}]}}\n\n")) + "data: [DONE]\n\n";
        LocalModel.Http = new HttpClient(new StreamOf(chunks));
        int shown = 0, offUi = 0;
        var last = "";
        var start = DateTime.UtcNow;
        var result = await LocalModel.CompleteAsync(new Uri("http://x.model.test"), "m", "s", "u", onText: t =>
        {
            shown++;
            last = t;
            if (Environment.CurrentManagedThreadId != UiThread.Id) offUi++;
        });
        var seconds = (DateTime.UtcNow - start).TotalSeconds;
        Assert.Equal(0, offUi);
        Assert.Equal(result.Text, last);    // the last piece is there when the answer returns
        Assert.Contains("w3999", last);
        Assert.True(shown <= seconds * 10 + 2, $"{shown} times in {seconds} s");
    });

    [Fact]
    public Task AnAnswerThatIsCutOffIsSaid() => UiThread.Run(async () =>
    {
        Studio.NewDocument("Tekst");
        Studio.Text = "# Een\n\nTekst.\n";
        LocalModel.Http = new HttpClient(new StreamOf("data: {\"choices\":[{\"delta\":{\"content\":\"Half\"},\"finish_reason\":\"length\"}],\"timings\":{\"predicted_per_second\":12.5}}\n\ndata: [DONE]\n\n"));
        Studio.Apply(QuickAction.Spelling);
        await WaitUntilDone();
        Assert.Contains("afgebroken", Assert.Single(Studio.Notes));
    });

    [Fact]
    public Task AnEngineThatRefusesIsSaidInItsOwnWords() => UiThread.Run(async () =>
    {
        LocalModel.Http = new HttpClient(new StreamOf("kapot", HttpStatusCode.InternalServerError));
        var e = await Assert.ThrowsAsync<Zip.Failure>(() => LocalModel.CompleteAsync(new Uri("http://x.model.test"), "m", "s", "u"));
        Assert.Equal("Engine gaf 500: kapot", e.Message);
    });

    private sealed class StreamOf(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }
}
