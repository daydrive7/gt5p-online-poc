# Gran Turismo 5 Prologue Online Server Proof of Concept

This is an unofficial, standalone C#/.NET 8 server proof of concept for Gran Turismo 5 Prologue. Spec II and Spec III are separate server implementations with independent settings and data. The source exposes experimental handlers for discovery, sessions, rankings, replays and matchmaking. Game-client compatibility and feature coverage vary by version.

The sample discovery lists advertise three regions: Americas (`us`), Europe (`eu`), and Japan (`jp`). The UK is grouped under Europe.

| Version | Project directory | Default HTTP URL |
| --- | --- | --- |
| Spec II | spec-ii | http://127.0.0.1:8180 |
| Spec III | spec-iii | http://127.0.0.1:8181 |

## Tested game editions

- EU Spec II physical copy, without the Spec III upgrade
- EU Spec III digital copy

## Run locally

Install the .NET 8 SDK, enter the project directory for the version you want, and run:

    dotnet run --project Gt5pOnlineServer.csproj

From another terminal, request /init/regionlist.xml or /init/eu/serverlist.xml on the corresponding URL. Both projects use Entity Framework Core for data access. Each starts with an in-memory database. Set the ConnectionStrings__GrimDb environment variable to use PostgreSQL for persistent storage.

These builds are for controlled local testing. The HTTP listener binds to loopback by default, and DNS, TLS, dynamic weather and the experimental real-time UDP listener are opt-in. The admin interface has no authentication and can display client data. Keep the server on an isolated test network if you configure it for console access.

Each Spec includes examples for server discovery, one event and its settings, news categories, and weather cities. The event and news examples show the formats used by these builds. Each news example links a category and list to news item 500. Edit the corresponding `resources/news/500.xml` to change the announcement. Additional content, including GT-TV media, requires suitable version-specific resources supplied separately. Console testing requires a separately configured compatible client.

## Credits and license

The server source is available under the MIT License. Credit to Nenkai for [Grimoire](https://github.com/Nenkai/Grimoire) and its work on the Grim protocol, including the NP ticket format, XML envelopes, and route shapes used as references for this project. Grimoire's MIT notice is included in THIRD-PARTY-NOTICES.md. See LICENSE for this project's license.

Gran Turismo is a trademark of its respective owner. This community project is unaffiliated with Sony Interactive Entertainment or Polyphony Digital.
