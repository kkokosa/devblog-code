using System;
using System.Globalization;
using System.Linq;
using System.Text;
using OpenAI.Chat;
using Spectre.Console;

// Available models (from platform.openai.com/docs/models)
var models = new[] { "gpt-4o-mini", "gpt-4o", "gpt-4.1", "gpt-5.2" };
var showLogits = args.Contains("--show-logits");
int topAlternatives = 3;
if (args.Contains("--top-alternatives"))
{
    var index = Array.IndexOf(args, "--top-alternatives");
    if (index >= 0 && index < args.Length - 1 && int.TryParse(args[index + 1], out var val))
    {
        topAlternatives = Math.Clamp(val, 1, 10);
    }
}

var currentModel = "gpt-4o-mini";
float currentTemperature = 1.0f;

// Predefined queries
var predefinedQueries = new (string Label, string Query)[]
{
    ("🎲 Random number", "Pick a random number from 0 to 100"),
    ("🐱 Cat name", "What's good name for a cat?"),
    ("🧭 Cardinal direction", "Pick a cardinal direction."),
    ("⌨️ Tabs vs spaces", "Tabs or spaces? Single word answer."),
    ("🇵🇱 Poland president", "Who is president of Poland?"),
    ("🤔 It depends...", "Well, it depends on how you look at it, doesn't it?"),
    ("✏️ Custom query...", "")
};

ChatClient? client = null;

while (true)
{
    AnsiConsole.Clear();
    AnsiConsole.Write(new FigletText("LogProbs").Color(Color.Blue));
    AnsiConsole.MarkupLine($"[dim]Model: [/][bold]{currentModel}[/]  [dim]Temperature: [/][bold]{currentTemperature:0.0}[/]");
    AnsiConsole.WriteLine();

    // Build menu choices with query text in parentheses
    var menuChoices = predefinedQueries
        .Select(q => string.IsNullOrEmpty(q.Query) 
            ? q.Label 
            : $"{q.Label} [dim]({Truncate(q.Query, 40)})[/]")
        .Concat(new[] { 
            "⚙️ Change model", 
            "🌡️ Change temperature", 
            $"🔢 Change top alternatives (current: {topAlternatives})",
            $"{(showLogits ? "✅" : "⬜")} Show raw logits",
            "🚪 Exit" 
        })
        .ToList();

    // Menu
    var action = AnsiConsole.Prompt(
        new SelectionPrompt<string>()
            .Title("[blue]What would you like to do?[/]")
            .PageSize(20)
            .AddChoices(menuChoices));

    if (action == "🚪 Exit")
        break;

    if (action.Contains("Show raw logits"))
    {
        showLogits = !showLogits;
        continue;
    }

    if (action.StartsWith("🔢 Change top alternatives"))
    {
        topAlternatives = AnsiConsole.Prompt(
            new TextPrompt<int>("[blue]Number of alternatives[/] [dim](1 - 10)[/]:")
                .DefaultValue(topAlternatives)
                .Validate(n => n >= 1 && n <= 10
                    ? ValidationResult.Success() 
                    : ValidationResult.Error("[red]Must be between 1 and 10[/]")));
        continue;
    }

    if (action == "⚙️ Change model")
    {
        currentModel = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[blue]Select model:[/]")
                .PageSize(10)
                .AddChoices(models));
        client = null; // Force recreate client
        continue;
    }

    if (action == "🌡️ Change temperature")
    {
        currentTemperature = AnsiConsole.Prompt(
            new TextPrompt<float>("[blue]Temperature[/] [dim](0.0 - 2.0)[/]:")
                .DefaultValue(currentTemperature)
                .Validate(t => t >= 0 && t <= 2 
                    ? ValidationResult.Success() 
                    : ValidationResult.Error("[red]Must be between 0.0 and 2.0[/]")));
        continue;
    }

    // Get query - find matching predefined or custom
    string query;
    if (action.StartsWith("✏️ Custom query"))
    {
        query = AnsiConsole.Prompt(
            new TextPrompt<string>("[blue]Enter your query:[/]")
                .AllowEmpty());
        if (string.IsNullOrWhiteSpace(query))
            continue;
    }
    else
    {
        // Find the matching predefined query by label prefix
        var match = predefinedQueries.FirstOrDefault(q => action.StartsWith(q.Label));
        query = match.Query;
        if (string.IsNullOrEmpty(query))
            continue;
    }

    // Create client if needed
    client ??= new ChatClient(currentModel, Environment.GetEnvironmentVariable("OPENAI_API_KEY"));

    // Configure logprobs and temperature
    var options = new ChatCompletionOptions
    {
        IncludeLogProbabilities = true,
        TopLogProbabilityCount = Math.Clamp(topAlternatives + 2, 5, 20),
        Temperature = currentTemperature
    };

    var messages = new ChatMessage[]
    {
        new UserChatMessage(query)
    };

    // Execute with spinner
    ChatCompletion? completion = null;
    await AnsiConsole.Status()
        .StartAsync("Thinking...", async ctx =>
        {
            completion = await client.CompleteChatAsync(messages, options);
        });

    // Display the query first
    AnsiConsole.Write(new Rule("[bold blue]Query[/]").RuleStyle("grey"));
    AnsiConsole.MarkupLine($"[italic]{Markup.Escape(query)}[/]");
    AnsiConsole.WriteLine();

    // Build annotated response with inline cues
    AnsiConsole.Write(new Rule("[bold blue]Response[/]").RuleStyle("grey"));

    if (completion?.ContentTokenLogProbabilities != null)
    {
        var tokens = completion.ContentTokenLogProbabilities.ToList();
        var annotatedResponse = new StringBuilder();

        foreach (var tokenLogProb in tokens)
        {
            double probability = Math.Exp(tokenLogProb.LogProbability);
            string color = GetProbabilityColor(probability);
            string escapedToken = Markup.Escape(tokenLogProb.Token);
            
            // Check for cues
            var cues = new List<string>();
            
            // Uncertainty cue - low probability token
            if (probability < 0.10)
            {
                cues.Add($"⚠️ low confidence {FormatPct(probability)}");
            }
            
            if (tokenLogProb.TopLogProbabilities != null && tokenLogProb.TopLogProbabilities.Count >= 2)
            {
                var topAlts = tokenLogProb.TopLogProbabilities.ToList();
                var mostProbable = topAlts[0];
                double mostProbableProb = Math.Exp(mostProbable.LogProbability);
                double secondProb = Math.Exp(topAlts[1].LogProbability);
                
                bool isNotTopChoice = mostProbable.Token != tokenLogProb.Token;
                // Ambiguous if second option has >15% probability (meaningful alternative)
                bool isAmbiguous = secondProb > 0.15;
                
                // Calculate significance: how big is the "not top" gap vs "ambiguity" closeness
                double notTopGap = mostProbableProb - probability;  // how far from top choice
                double ambiguityCloseness = mostProbableProb - secondProb;  // how close are top 2
                
                // If both apply, pick the more significant one
                if (isNotTopChoice && isAmbiguous)
                {
                    // Large gap to top = more "not top"; small gap between top 2 = more "ambiguous"
                    if (notTopGap > ambiguityCloseness)
                    {
                        isAmbiguous = false;  // show only "not top"
                    }
                    else
                    {
                        isNotTopChoice = false;  // show only "ambiguous"
                    }
                }
                
                // Not-top-choice cue - selected token is not the most probable
                if (isNotTopChoice)
                {
                    cues.Add($"🎯 top was '{Markup.Escape(mostProbable.Token)}' {FormatPct(mostProbableProb)}");
                }
                
                // Ambiguity cue - close alternatives exist
                if (isAmbiguous)
                {
                    // Get alternatives with >5% probability (excluding selected token)
                    var closeAlts = topAlts
                        .Where(alt => alt.Token != tokenLogProb.Token)
                        .Where(alt => Math.Exp(alt.LogProbability) > 0.05)
                        .Take(3)
                        .Select(alt => $"{Markup.Escape(alt.Token)} {FormatPct(Math.Exp(alt.LogProbability))}")
                        .ToList();
                    
                    if (closeAlts.Count > 0)
                    {
                        // Use [[ and ]] to escape brackets in Spectre.Console markup
                        cues.Add($"🔀 vs [[{string.Join(", ", closeAlts)}]]");
                    }
                }
            }

            // Build token with optional annotation
            if (cues.Count > 0)
            {
                annotatedResponse.Append($"[bold underline {color}]{escapedToken}[/][dim]({string.Join(" ", cues)})[/]");
            }
            else
            {
                annotatedResponse.Append($"[{color}]{escapedToken}[/]");
            }
        }

        AnsiConsole.MarkupLine(annotatedResponse.ToString());
    }
    else
    {
        AnsiConsole.MarkupLine($"[bold]{Markup.Escape(completion?.Content[0].Text ?? "No response")}[/]");
    }

    AnsiConsole.WriteLine();

    // Access logprobs directly from the completion
    if (completion?.ContentTokenLogProbabilities != null)
    {
        AnsiConsole.Write(new Rule("[bold blue]Token Log Probabilities[/]").RuleStyle("grey"));
        AnsiConsole.WriteLine();

        var tokens = completion.ContentTokenLogProbabilities.ToList();
        
        // Split into chunks that fit nicely (max ~8 tokens per table)
        const int tokensPerTable = 8;
        var chunks = tokens.Chunk(tokensPerTable).ToList();

        foreach (var chunk in chunks)
        {
            var table = new Table()
                .Border(TableBorder.Rounded)
                .BorderColor(Color.Grey)
                .ShowHeaders();

            // Add columns with selected token as header
            foreach (var tokenLogProb in chunk)
            {
                double probability = Math.Exp(tokenLogProb.LogProbability);
                string color = GetProbabilityColor(probability);
                string escapedToken = Markup.Escape(tokenLogProb.Token);
                var header = $"[bold {color}]{escapedToken}[/] [dim]{FormatPct(probability)}[/]";
                table.AddColumn(new TableColumn(header).LeftAligned());
            }

            // Row: Top alternatives (excluding the selected token)
            var alternativesRow = new List<string>();
            foreach (var tokenLogProb in chunk)
            {
                if (tokenLogProb.TopLogProbabilities != null)
                {
                    // Filter out the selected token, then take top alternatives
                    var alternatives = tokenLogProb.TopLogProbabilities
                        .Where(alt => alt.Token != tokenLogProb.Token)
                        .Take(topAlternatives)
                        .ToList();

                    if (alternatives.Count > 0)
                    {
                        var altText = string.Join("\n", alternatives.Select(alt =>
                        {
                            double altProb = Math.Exp(alt.LogProbability);
                            string altColor = GetProbabilityColor(altProb);
                            string escapedAlt = Markup.Escape(alt.Token);
                            return $"[{altColor}]{escapedAlt}[/] [dim]{FormatPct(altProb)}[/]";
                        }));
                        alternativesRow.Add(altText);
                    }
                    else
                    {
                        alternativesRow.Add("[dim]-[/]");
                    }
                }
                else
                {
                    alternativesRow.Add("[dim]-[/]");
                }
            }
            table.AddRow(alternativesRow.ToArray());

            AnsiConsole.Write(table);
            AnsiConsole.WriteLine();
        }
    }

    if (showLogits && completion?.ContentTokenLogProbabilities != null)
    {
        AnsiConsole.Write(new Rule("[bold blue]Raw Logits[/]").RuleStyle("grey"));
        AnsiConsole.WriteLine();

        var tokens = completion.ContentTokenLogProbabilities.ToList();
        
        // Split into chunks that fit nicely (max ~8 tokens per table)
        const int tokensPerTable = 8;
        var chunks = tokens.Chunk(tokensPerTable).ToList();

        foreach (var chunk in chunks)
        {
            var table = new Table()
                .Border(TableBorder.Rounded)
                .BorderColor(Color.Grey)
                .ShowHeaders();

            // Add columns with selected token as header
            foreach (var tokenLogProb in chunk)
            {
                double probability = Math.Exp(tokenLogProb.LogProbability);
                string color = GetProbabilityColor(probability);
                string escapedToken = Markup.Escape(tokenLogProb.Token);
                var header = $"[bold {color}]{escapedToken}[/] [dim]{tokenLogProb.LogProbability:F2}[/]";
                table.AddColumn(new TableColumn(header).LeftAligned());
            }

            // Row: Top alternatives (excluding the selected token)
            var alternativesRow = new List<string>();
            foreach (var tokenLogProb in chunk)
            {
                if (tokenLogProb.TopLogProbabilities != null)
                {
                    // Filter out the selected token, then take top alternatives
                    var alternatives = tokenLogProb.TopLogProbabilities
                        .Where(alt => alt.Token != tokenLogProb.Token)
                        .Take(topAlternatives)
                        .ToList();

                    if (alternatives.Count > 0)
                    {
                        var altText = string.Join("\n", alternatives.Select(alt =>
                        {
                            double altProb = Math.Exp(alt.LogProbability);
                            string altColor = GetProbabilityColor(altProb);
                            string escapedAlt = Markup.Escape(alt.Token);
                            return $"[{altColor}]{escapedAlt}[/] [dim]{alt.LogProbability:F2}[/]";
                        }));
                        alternativesRow.Add(altText);
                    }
                    else
                    {
                        alternativesRow.Add("[dim]-[/]");
                    }
                }
                else
                {
                    alternativesRow.Add("[dim]-[/]");
                }
            }
            table.AddRow(alternativesRow.ToArray());

            AnsiConsole.Write(table);
            AnsiConsole.WriteLine();
        }
    }

    // Wait for user before continuing
    AnsiConsole.MarkupLine("[dim]Press any key to continue...[/]");
    Console.ReadKey(true);
}

static string FormatPct(double value) => $"{value * 100:0}%";

static string Truncate(string text, int maxLength) => 
    text.Length <= maxLength ? text : text[..(maxLength - 3)] + "...";

static string GetProbabilityColor(double probability)
{
    return probability switch
    {
        >= 0.9 => "green",
        >= 0.7 => "lime",
        >= 0.5 => "yellow",
        >= 0.3 => "orange1",
        _ => "red"
    };
}
