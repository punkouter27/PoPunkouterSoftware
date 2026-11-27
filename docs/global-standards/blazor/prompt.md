# .NET Blazor Apps & Games — Global Audit Prompt

Use this prompt when you are ready to crawl all `.NET` / Blazor apps and games under `C:\Users\punko\Downloads\_CODEMATT` and populate `GLOBAL/blazor_apps/`:

```text
Act as an expert .NET/Blazor Solution Architect, Game Developer, and Frontend Engineer.

Analyze all my sub-folders under C:\Users\punko\Downloads\_CODEMATT and find my .NET Blazor apps and games (identifying Blazor Server/WebAssembly, .razor components, C# game loops, and UI files). Create a comprehensive, single-file HTML report at GLOBAL/blazor_apps/blazor_overview.html (plus a reusable crawler/builder in GLOBAL/blazor_apps/scripts/ and a time-based tracker in GLOBAL/blazor_apps/history/blazor_progress_over_time.html) broken down into 3 levels:

LEVEL 1: ARCHITECTURE & HIGH-LEVEL GRID (Overview)
- Global master grid comparing all Blazor projects: Project Name, Hosting Model (Server/WASM), Primary Type (Utility, Game, Dashboard), Interactive Mode (Server, WebAssembly, Auto, Static), Key NuGet Dependencies, and Active State.
- Visual KPIs: Total App Count, Hosting Model Breakdown, Game vs. Line-of-Business ratio, Total Components Mapped.

LEVEL 2: UI, STATE, & GAME FLOW (The "What" & Component Diagrams)
- Expandable detail view for each project:
  - Component Hierarchy Diagram (visual SVG/CSS tree of MainLayout -> Pages -> Components -> GameLoop)
  - Page & Route Inventory (`@page "/..."`)
  - State Management & Services (DI Services, cascading parameters, local storage, game state)
  - Game Loops & Canvas Details (update/render cycle, tick rate, inputs, rendering tech)

LEVEL 3: TECHNICAL DETAILS & PERFORMANCE (Specs & Code)
- File/LOC breakdown (C# vs. Razor vs. JS interop)
- Component communication types (EventCallbacks, Services, JSInterop)
- Dependency Injection matrix (Scoped, Transient, Singleton)
- Performance & Optimization (WASM AOT, prerendering, lifecycle methods)
- Key Files to inspect (clickable relative links)

OUTPUT REQUIREMENTS:
- Single offline-ready HTML file matching the dark theme of GLOBAL/index.html, with interactive search/filtering, sorting, and collapsible sections.
- Include a one-click updater (GLOBAL/blazor_apps/update_blazor.bat) and link GLOBAL/index.html to blazor_apps/blazor_overview.html and blazor_apps/history/blazor_progress_over_time.html.
```
