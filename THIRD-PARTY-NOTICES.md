# Third-party notices

MockProxyGateway is MIT licensed. Its runtime NuGet package references the following
independently distributed dependencies; their assemblies are not bundled in the Core
package. Their respective copyright notices and license terms accompany their packages.

| Runtime dependency | Locked version | Package license |
| --- | --- | --- |
| [Yarp.ReverseProxy](https://www.nuget.org/packages/Yarp.ReverseProxy/2.3.0) | 2.3.0 | MIT |
| [JsonPath.Net](https://www.nuget.org/packages/JsonPath.Net/2.2.0) | 2.2.0 | MIT |
| [Json.More.Net](https://www.nuget.org/packages/Json.More.Net/2.2.0) | 2.2.0 | MIT |
| [System.IO.Hashing](https://www.nuget.org/packages/System.IO.Hashing/8.0.0) | 8.0.0 | MIT |

The exact resolved runtime licenses were read from restored NuGet package metadata
in [CI run 45548822269](https://github.com/ginmp8/MockProxyGateway/actions/runs/45548822269)
(commit 2882218dcf9633c5b75ac041ecf0fa08785fadfe).
The package inspection gate checks the license expressions of every runtime dependency
on each build. Review any changed license before distribution. The ASP.NET Core shared
framework is supplied by the consuming host; its licenses are distributed with .NET.
Test-only dependencies are not shipped in Core.

JsonPath.Net 2.2.0 and its matching Json.More.Net version are deliberate MIT-compatible
pins. Do not assume that subsequent binary releases have the same license.
