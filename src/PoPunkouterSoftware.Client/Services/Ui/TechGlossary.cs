namespace PoPunkouterSoftware.Client;

/// <summary>
/// What each piece of technology on the home page matrix IS, in one plain sentence for a
/// visitor who has never heard of it — the first half of every hint there (owner's ask,
/// 2026-11-03: "a tooltip with more details on any section and text"). The second half, how
/// one app uses it, is that app's <c>stack.details</c> note in apps.json.
///
/// Keyed on the short labels apps.json uses. A label with a version ("MuJoCo 3.10",
/// "Unity ML-Agents 4.1") falls back to its longest listed prefix, and a combined label
/// ("MuJoCo Warp + PPO (PyTorch)") is described part by part. A new term in apps.json with
/// no entry here still renders, just without the "what it is" sentence — add it here.
/// </summary>
public static class TechGlossary
{
    /// <summary>What each column and each Tech stack line means.</summary>
    public static readonly IReadOnlyDictionary<string, string> Sections = new Dictionary<string, string>
    {
        ["app"] = "The app's name, a picture of it and what it does. The name opens the live app in a new tab.",
        ["signIn"] = "How a visitor signs in to the app, or None when it has no login.",
        ["techstack"] = "What the app is built from: what runs in the browser, what runs on the server, how it pushes live updates and where it keeps its data. For a game: the engine, the physics and how its AI was trained.",
        ["ai"] = "Every AI model the app uses, grouped by where it runs: Azure, Google, or inside the app itself.",
        ["delivery"] = "How new code gets from GitHub to the live site.",
        ["extras"] = "Libraries and services that only this app uses.",
        ["shared"] = "These are the same for every app in this table, so they are listed once here instead of filling a column.",
        ["frontend"] = "What runs in the visitor's browser and draws the page.",
        ["engine"] = "The game engine: what draws the game and runs its logic.",
        ["backend"] = "What runs on the server: the API the page calls.",
        ["realtime"] = "How the server pushes updates to the page the moment they happen, without the page asking again.",
        ["data"] = "Where the app keeps its data.",
        ["physics"] = "What simulates the bodies, so they move, collide and fall like real objects.",
        ["training"] = "How the game's AI players learned, by reinforcement learning: thousands of practice matches, rewarded for winning.",
    };

    private static readonly Dictionary<string, string> Terms = new(StringComparer.OrdinalIgnoreCase)
    {
        // Frontend / engine / backend
        ["Blazor WASM"] = "Microsoft's Blazor running on WebAssembly: the page is written in C# and runs inside the browser, not JavaScript.",
        ["Unity"] = "Unity, a game engine. This game is built for WebGL, so it plays in a browser with nothing to install.",
        ["Unreal Engine"] = "Epic's Unreal Engine, the game engine behind many console and PC games.",
        ["ASP.NET Core"] = "Microsoft's web server framework for C#. It serves the page and the API behind it.",

        // Real-time
        ["SignalR"] = "Microsoft's library for live two-way messages between server and browser.",
        ["WebSockets"] = "A browser connection that stays open, so server and page can send messages to each other at any time.",
        ["Server-sent events"] = "A one-way stream from the server: the page stays connected and receives updates as they happen.",
        ["Web Push"] = "Browser notifications the server can send even when the page is closed.",

        // Data
        ["Azure Table Storage"] = "Azure's low-cost key-value store for structured records.",
        ["Azure Blob Storage"] = "Azure's file storage, for images, audio, video and other files.",
        ["Azure Storage"] = "An Azure storage account: tables, files and queues.",

        // Sign-in and delivery
        ["Microsoft"] = "Sign in with a Microsoft account (personal or work), through Microsoft Entra ID.",
        ["GitHub"] = "Sign in with a GitHub account.",
        ["None"] = "No login: anyone can use it straight away.",
        ["GitHub Actions"] = "GitHub's automation: every push builds the app, runs its tests and deploys it to Azure.",

        // AI, where the app runs it
        ["Azure"] = "Models hosted in Microsoft Azure (Azure OpenAI / AI Foundry), called from the app's server.",
        ["Google"] = "Models hosted by Google, called through the Gemini API.",
        ["In-app"] = "Models that run inside the app itself, in the browser or on its own server, with no AI cloud service involved.",
        ["Azure OpenAI"] = "OpenAI's models (GPT and others) hosted in Microsoft Azure.",
        ["AI Foundry"] = "Azure AI Foundry, Microsoft's hub for hosted AI models from OpenAI and others.",
        ["OpenAI"] = "OpenAI's GPT models.",
        ["OpenAI / Gemini"] = "OpenAI's GPT models and Google's Gemini models.",
        ["Gemini"] = "Google's Gemini family of AI models.",
        ["On-device"] = "AI that runs on the visitor's own device, not in the cloud.",
        ["RL agents"] = "AI players trained by reinforcement learning; they run inside the game.",

        // AI models
        ["GPT-5"] = "A model from OpenAI's GPT-5 family. Mini and nano versions are smaller, faster and cheaper.",
        ["GPT-4.1"] = "A model from OpenAI's GPT-4.1 family. Mini and nano versions are smaller, faster and cheaper.",
        ["GPT-4o"] = "OpenAI's GPT-4o model, which reads both text and images.",
        ["Azure AI Vision"] = "Azure's image-analysis service: it recognises objects, scenes and text in pictures.",
        ["Speech transcription"] = "Speech-to-text: it turns what people say into written words.",
        ["GPT-4o Transcribe"] = "OpenAI's speech-to-text model: it turns recorded speech into text.",
        ["GPT-4o mini TTS"] = "OpenAI's text-to-speech model: it reads text aloud in a chosen voice.",
        ["GPT-Realtime"] = "OpenAI's live voice model: it listens and talks back in real time.",
        ["Whisper"] = "OpenAI's open speech-recognition model, which turns speech into text.",
        ["Whisper tiny"] = "The smallest version of OpenAI's Whisper speech-recognition model, small enough to run in a browser.",
        ["Whisper.net"] = "Whisper speech recognition for .NET (built on whisper.cpp), run on the app's own server.",
        ["FLUX.1 Kontext"] = "Black Forest Labs' image model, which edits a picture from a text instruction.",
        ["AI Content Safety"] = "Azure's moderation service: it flags harmful text and images.",
        ["Gemini 2.5 Flash Image"] = "Google's Gemini image model, which creates and edits pictures from a text prompt.",
        ["Gemini Flash-Lite"] = "The smallest, fastest and cheapest tier of Google's Gemini models.",
        ["Gemini 2.5 Flash-Lite"] = "The smallest, fastest and cheapest tier of Google's Gemini 2.5 models.",
        ["Gemini 3.5 Flash-Lite"] = "The smallest, fastest and cheapest tier of Google's Gemini 3.5 models.",
        ["Gemini Live"] = "Google's Gemini live voice, speech and web-search features.",
        ["Gemini Omni"] = "A Google Gemini model, called through the Gemini API.",
        ["Lyria"] = "Google's music-generation model: it composes music from a text prompt.",
        ["ONNX audio models"] = "Audio AI models in the open ONNX format, run inside the app.",
        ["Reinforcement-learning agents"] = "AI players trained by reinforcement learning, run inside the game.",
        ["WebLLM browser models"] = "Language models that run entirely in the visitor's browser on the GPU, through WebLLM.",

        // Physics and training
        ["MuJoCo"] = "MuJoCo, a physics engine built for robotics research: accurate joints, contacts and muscles.",
        ["MuJoCo Warp"] = "MuJoCo rebuilt for NVIDIA GPUs, so thousands of copies of the game can be simulated at once during training.",
        ["MuJoCo Playground"] = "Google DeepMind's ready-made robot-training environments built on MuJoCo.",
        ["Unity physics"] = "Unity's built-in physics engine (NVIDIA PhysX).",
        ["Unity ML-Agents"] = "Unity's toolkit for training game characters with reinforcement learning.",
        ["ML-Agents built from source"] = "Unity ML-Agents compiled from its source code instead of installed as a package.",
        ["PPO"] = "Proximal Policy Optimization, the most widely used reinforcement-learning method.",
        ["PPO (PyTorch)"] = "Proximal Policy Optimization, the most widely used reinforcement-learning method, written in PyTorch.",
        ["rsl_rl PPO"] = "ETH Zurich's rsl_rl implementation of PPO, popular for training robots to walk.",
        ["Brax PPO"] = "Google's Brax implementation of PPO, which runs on JAX for fast GPU training.",
        ["PyTorch"] = "Meta's open-source machine-learning library for Python.",
        ["JAX"] = "Google's machine-learning library for fast maths on GPUs.",

        // Unique tech
        ["2D Tilemap"] = "Unity's tool for painting 2D levels out of grid tiles.",
        ["AI Navigation"] = "Unity's pathfinding: characters find their way around obstacles.",
        ["AcoustID"] = "An open audio-fingerprint service that identifies a song from its sound.",
        ["Azure email"] = "Azure Communication Services Email, for sending email from the app.",
        ["Burst"] = "Unity's compiler that turns C# into fast native machine code.",
        ["Cinemachine"] = "Unity's camera system: it follows the action and frames shots automatically.",
        ["DryWetMidi"] = "A .NET library for reading, writing and playing MIDI music files.",
        ["GitHub API"] = "GitHub's web API, for reading repositories, commits and other data.",
        ["Google Maps API"] = "Google's maps, routes and travel times, used from code.",
        ["Google Places API"] = "Google's database of places: businesses, addresses and reviews.",
        ["Leaflet maps"] = "Leaflet, an open-source JavaScript library for interactive maps.",
        ["LibGit2Sharp"] = "A .NET library that reads and writes git repositories directly.",
        ["MessagePack"] = "A compact binary format that makes messages smaller and faster than JSON.",
        ["MessagePipe"] = "A fast in-process messaging library for .NET and Unity.",
        ["NNE (ONNX Runtime)"] = "Unreal's Neural Network Engine, which runs trained AI models inside the game.",
        ["ONNX Runtime"] = "Microsoft's engine for running trained AI models saved in the open ONNX format.",
        ["Opus audio (Concentus)"] = "Concentus, a C# implementation of the Opus audio codec used for voice.",
        ["Pixel Streaming 2"] = "Unreal renders the game on a server and streams the video to a browser.",
        ["PrimeTween"] = "A fast Unity library for smooth animations and transitions.",
        ["QRCoder"] = "A .NET library that generates QR codes.",
        ["QuestPDF"] = "A .NET library for generating PDF documents.",
        ["Recorder"] = "Unity's Recorder, which captures gameplay as video or images.",
        ["Roslyn"] = "Microsoft's C# compiler as a library: it reads, analyses and compiles code.",
        ["SharpGLTF"] = "A .NET library for reading and writing glTF 3D models.",
        ["SkiaSharp"] = "Google's Skia 2D graphics engine for .NET: drawing, images and text.",
        ["Tone.js"] = "A JavaScript framework for making music and sound in the browser.",
        ["UniTask"] = "Fast, allocation-free async/await for Unity.",
        ["Unitree Go2 and G1 models"] = "3D models of Unitree's real robots: the Go2 robot dog and the G1 humanoid.",
        ["Unity MCP"] = "A Model Context Protocol server that lets an AI coding assistant work in the Unity editor.",
        ["Unreal Robotics Lab"] = "An Unreal Engine plugin that runs MuJoCo robot physics inside Unreal.",
        ["VContainer"] = "A dependency-injection library for Unity: it wires the game's parts together.",
        ["VFX Graph"] = "Unity's GPU particle system for effects like sparks, smoke and dust.",
        ["WebGPU"] = "The modern browser API for the graphics card, fast enough to run AI models.",
        ["WebLLM"] = "Runs large language models entirely in the browser, on the visitor's GPU.",
        ["glTFast"] = "A fast Unity loader for glTF 3D models.",
        ["three.js"] = "The most popular JavaScript library for 3D graphics in the browser.",
        ["transformers.js"] = "Hugging Face's library for running AI models directly in the browser.",
        ["tus uploads"] = "tus, an open protocol for resumable uploads: a big file survives a dropped connection.",
    };

    /// <summary>"What it is" for a cell value, or null when nothing here describes it.</summary>
    public static string? Describe(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var terms = value.Split(" + ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 1) return DescribeOne(terms[0]);
        // Several pieces in one label: name each, or the sentences run together.
        var parts = terms.Select(t => DescribeOne(t) is { } d ? $"{t}: {d}" : null).OfType<string>().ToArray();
        return parts.Length == 0 ? null : string.Join(" ", parts);
    }

    private static string? DescribeOne(string term)
    {
        if (Terms.TryGetValue(term, out var exact)) return exact;
        // "MuJoCo 3.10" → "MuJoCo", "GPT-5.4 mini" → "GPT-5": the longest key that is a whole-word
        // prefix (the next character is not a letter, so "Unity" never matches "UniTask").
        return Terms
            .Where(t => term.StartsWith(t.Key, StringComparison.OrdinalIgnoreCase)
                        && !char.IsLetter(term[t.Key.Length]))
            .OrderByDescending(t => t.Key.Length)
            .Select(t => t.Value)
            .FirstOrDefault();
    }

    /// <summary>A hint's full text: what it is, then how this app uses it.</summary>
    public static string? Hint(string value, string? note, string? appName)
    {
        var what = Describe(value);
        if (string.IsNullOrWhiteSpace(note) || note == value) return what;
        note = note.TrimEnd();
        if (!".!?)".Contains(note[^1])) note += ".";
        var how = appName is null ? note : $"In {appName}: {note}";
        return what is null ? how : $"{what} {how}";
    }
}
