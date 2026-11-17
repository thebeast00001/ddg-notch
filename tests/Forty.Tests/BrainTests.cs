using Forty.Core;

namespace Forty.Tests;

/// <summary>A model that answers from a script, records what it was sent, and can fail on purpose.</summary>
sealed class ScriptedModel(Func<IReadOnlyList<ChatMessage>, string> answer, ModelTrouble? fail = null) : IChatModel
{
    public string Name => "Scripted";
    public bool OnThisPc => true;
    public List<IReadOnlyList<ChatMessage>> Calls { get; } = new();

    public Task<ChatReply> CompleteAsync(IReadOnlyList<ChatMessage> messages, ChatOptions options, CancellationToken cancel)
    {
        Calls.Add(messages);
        if (fail is { } trouble) throw new ModelException(trouble, Name);
        return Task.FromResult(new ChatReply(answer(messages), 0.4, Name));
    }
}

public static class BrainTests
{
    [Test]
    public static void Only_a_skill_from_the_table_with_the_right_arguments_is_a_decision()
    {
        var open = DecisionParser.Parse("{\"agent\": \"bolt\", \"skill\": \"open_app\", \"args\": {\"app\": \"Spotify\"}}");
        Check.True(open is { Argument: "Spotify" } && open.Skill == Skills.OpenApp && open.Agent == Cast.Bolt, "open an app");
        Check.Equal("next", DecisionParser.Parse("Sure! ```json\n{\"agent\":\"bolt\",\"skill\":\"media\",\"args\":{\"action\":\"next\"}}\n```")?.Argument, "wrapped in a fence and words");
        Check.Equal("battery", DecisionParser.Parse("{\"agent\":\"iris\",\"skill\":\"tell\",\"args\":{\"fact\":\"battery\"}}")?.Argument, "a fact this PC knows");

        Check.True(DecisionParser.Parse("{\"agent\":\"bolt\",\"skill\":\"delete_files\",\"args\":{}}") == null, "a skill that isn't in the table");
        Check.True(DecisionParser.Parse("{\"agent\":\"iris\",\"skill\":\"open_app\",\"args\":{\"app\":\"Spotify\"}}") == null, "the right skill with the wrong agent");
        Check.True(DecisionParser.Parse("{\"agent\":\"bolt\",\"skill\":\"media\",\"args\":{\"action\":\"volume_up\"}}") == null, "an action the skill doesn't have");
        Check.True(DecisionParser.Parse("{\"agent\":\"iris\",\"skill\":\"tell\",\"args\":{\"fact\":\"password\"}}") == null, "a fact the PC doesn't tell");
        Check.True(DecisionParser.Parse("{\"agent\":\"bolt\",\"skill\":\"open_app\",\"args\":{}}") == null, "missing arguments");
        Check.True(DecisionParser.Parse("I think you want Spotify.") == null, "no JSON at all");

        string longAnswer = new string('a', 2000);
        var said = DecisionParser.Parse($"{{\"agent\":\"iris\",\"skill\":\"answer\",\"args\":{{\"text\":\"{longAnswer}\"}}}}");
        Check.True(said?.Say is { Length: <= DecisionParser.MaxAnswer }, "an answer is cut to a readable length");
        var oneLine = DecisionParser.Parse("{\"agent\":\"iris\",\"skill\":\"answer\",\"args\":{\"text\":\"one\\ntwo\\u0007\"}}");
        Check.Equal("one two", oneLine?.Say, "control characters and line breaks become spaces");
    }

    [Test]
    public static void The_prompt_is_built_from_the_skills_table_and_carries_nothing_personal()
    {
        string prompt = BrainPrompt.System();
        foreach (var skill in Skills.All) Check.True(prompt.Contains($"skill \"{skill.Id}\""), $"{skill.Id} is offered");
        foreach (var agent in Cast.All) Check.True(prompt.Contains(agent.Name), $"{agent.Name} is named");
        Check.True(!prompt.Contains("Users\\") && !prompt.Contains("@"), "no paths, no addresses");
    }

    [Test]
    public static void The_brain_decides_and_says_why_when_it_can_not()
    {
        var model = new ScriptedModel(_ => "{\"agent\":\"bolt\",\"skill\":\"open_app\",\"args\":{\"app\":\"something to edit photos\"}}");
        var brain = new Brain(model);
        var result = brain.DecideAsync("I want to touch up a picture", null, 0, CancellationToken.None).GetAwaiter().GetResult();
        Check.True(result.Decision?.Skill == Skills.OpenApp && result.Decision.Argument == "something to edit photos", "decided");
        Check.True(result.OnThisPc && result.Provider == "Scripted" && result.Seconds > 0, "who understood it, and how long it took");
        Check.True(result.DecisionJson?.Contains("open_app") == true, "remembered as JSON for the next request");

        var lost = new Brain(new ScriptedModel(_ => "", ModelTrouble.Unreachable)).DecideAsync("hi", null, 0, CancellationToken.None).GetAwaiter().GetResult();
        Check.True(lost.Decision == null && lost.Trouble == ModelTrouble.Unreachable, "an unreachable model is said plainly");
        Check.True(Brain.Explain(ModelTrouble.Refused, "Sarvam").Contains("key"), "a refused key says to check the key");
        var garbled = new Brain(new ScriptedModel(_ => "no idea")).DecideAsync("hi", null, 0, CancellationToken.None).GetAwaiter().GetResult();
        Check.True(garbled.Decision == null && garbled.Trouble == ModelTrouble.Unreadable, "an answer it can't read is not acted on");
    }

    [Test]
    public static void A_correction_within_a_minute_is_sent_with_what_came_before()
    {
        var model = new ScriptedModel(_ => "{\"agent\":\"bolt\",\"skill\":\"open_app\",\"args\":{\"app\":\"Edge\"}}");
        var brain = new Brain(model);
        var before = new Exchange("open a browser", "{\"agent\":\"bolt\",\"skill\":\"open_app\",\"args\":{\"app\":\"Chrome\"}}", 100);
        brain.DecideAsync("no, I meant Edge", before, 130, CancellationToken.None).GetAwaiter().GetResult();
        var sent = model.Calls[0];
        Check.True(sent.Count == 4 && sent[1].Content == "open a browser" && sent[2].Role == "assistant", "the last request and its decision go with it");
        brain.DecideAsync("open notepad", before, 200, CancellationToken.None).GetAwaiter().GetResult();
        Check.Equal(2, model.Calls[1].Count, "after a minute, a request stands on its own");
        brain.DecideAsync(new string('x', 3000), null, 0, CancellationToken.None).GetAwaiter().GetResult();
        Check.True(model.Calls[2][^1].Content.Length == Brain.MaxRequest, "a very long request is cut before it is sent");
    }

    [Test]
    public static void Quick_commands_need_no_model()
    {
        Check.True(RequestRules.Parse("pause the music") is { Kind: RequestKind.Media, Argument: "pause" }, "pause");
        Check.True(RequestRules.Parse("skip this song") is { Kind: RequestKind.Media, Argument: "next" }, "skip");
        Check.True(RequestRules.Parse("next") is { Kind: RequestKind.Media, Argument: "next" }, "next");
        Check.True(RequestRules.Parse("go back a track") is { Kind: RequestKind.Media, Argument: "previous" }, "previous");
        Check.True(RequestRules.Parse("what time is it?") is { Kind: RequestKind.Tell, Argument: "time" }, "the time");
        Check.True(RequestRules.Parse("how much battery is left") is { Kind: RequestKind.Tell, Argument: "battery" }, "the battery");
        Check.True(RequestRules.Parse("stop everything").Kind == RequestKind.PauseEverything, "the emergency pause still wins");
        Check.True(RequestRules.Parse("what is a metaball").Kind == RequestKind.Unknown, "a real question goes to the brain");
        Check.True(RequestRules.Parse("Iris, pause the music").Kind == RequestKind.Unknown, "an agent named for a job that isn't its own goes to the brain");
    }

    [Test]
    public static void Hey_Forty_turns_dictation_into_a_request()
    {
        Check.Equal("open Spotify", VoiceRequest.FromDictation("Hey Forty, open Spotify"), "a request");
        Check.Equal("what time is it?", VoiceRequest.FromDictation("ok forty what time is it?"), "OK Forty");
        Check.Equal("pause the music", VoiceRequest.FromDictation("Hey 40. pause the music"), "a number heard for the name");
        Check.True(VoiceRequest.FromDictation("Forty people came to the meeting") == null, "a sentence starting with Forty is still typed");
        Check.True(VoiceRequest.FromDictation("Hey Forty") == null, "the name alone asks nothing");
    }
}
