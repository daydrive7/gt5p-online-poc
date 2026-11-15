# Gran Turismo 5 Prologue Online Server Proof of Concept

This is an unofficial, standalone C#/.NET 8 server proof of concept for Gran Turismo 5 Prologue. Spec II and Spec III are separate server implementations with independent settings and data. The source exposes experimental handlers for discovery, sessions, rankings, replays and matchmaking. Game-client compatibility and feature coverage vary by version.

This repository publishes the older standalone builds. The newer GrimService integration was developed separately and has broader feature coverage. The sample discovery lists advertise three regions: Americas (`us`), Europe (`eu`), and Japan (`jp`). The UK is grouped under Europe.

| Version | Project directory | Default HTTP URL |
| --- | --- | --- |
| Spec II | spec-ii | http://127.0.0.1:8180 |
| Spec III | spec-iii | http://127.0.0.1:8181 |

## Run locally

Install the .NET 8 SDK, enter the project directory for the version you want, and run:

    dotnet run --project Gt5pOnlineServer.csproj

From another terminal, request /init/regionlist.xml or /init/eu/serverlist.xml on the corresponding URL. Each project starts with an in-memory database. Use the ConnectionStrings__GrimDb environment variable if you need PostgreSQL persistence.

These builds are for controlled local testing. The HTTP listener binds to loopback by default, and DNS, TLS, dynamic weather and the experimental real-time UDP listener are opt-in. The admin interface has no authentication and can display client data. Keep the server on an isolated test network if you configure it for console access.

The included XML is a synthetic discovery sample with an empty event list. Console testing requires a separately configured compatible client. Features such as online events, news and GT-TV require suitable version-specific resources supplied separately. This release contains server source and synthetic samples only.

## Credits and license

The server source is available under the MIT License. The ticket format work references [Nenkai/Grimoire](https://github.com/Nenkai/Grimoire) and retains its MIT notice. See LICENSE and THIRD-PARTY-NOTICES.md.

Gran Turismo is a trademark of its respective owner. This community project is unaffiliated with Sony Interactive Entertainment or Polyphony Digital.
