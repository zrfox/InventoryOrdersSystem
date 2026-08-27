# Linux image with .NET runtime named as base. (runtime = what runs compiled code)
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
# sets working dir in container to /app
WORKDIR /app
# exposes container on 8080, host listens on 5209 (5209 determined by .yml
EXPOSE 8080


# gets sdk as build. (sdk contains runtime & compilers, dotnet build/publish/restore tooling. Everything needed to produce the  compiled code originally.Runtime is engine to make car go, SDK is whole factory that builds the engine.
# sdk image is much bigiger (hundreds of MB), don't need once compiled. 
# final output will be handful of .dll files into the small base runtime image <- the purpose of the multi-stage build process [this entire file]
# (several FROM ... AS name stages where later stages can COPY --from=earlier_stage)
# FROM keyword starts fresh, isolated filesystem. 
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
# new workdir specific to this build stage
WORKDIR /src
# copy only .csproj so that we can do RUN dotnet restore to get the NUGET packages in a single, cached step that can be reused. 
# For COPY, this means Docker checks if copied files' contents hav echanged since last time.
# So, if .csproj here is byte-identical to last build, COPY layer is a cache hit. 
# If cache hit, everything up to the next changed instruction is also reused. 
# if you change a .cs file after a build, that won't affect this but means COPY . . will be a cache miss
# and would have to rebuild from that point. 
COPY ["InventoryService.csproj", "./"]
# each instruction/layer in docker is cached. dotnet restore  = download NuGet packages, which are large & often slower than compiling
# this layer can be reused in other builds so you don't have to download again.
RUN dotnet restore "InventoryService.csproj"
# copy everything else (all actual source files) into /src
COPY . .
# publish compiles and produces DLLs for local running/debugging like build, but pusblish gathers
# everything for standard deployment: copies oover dependencies, trims/optimizes for a deployment target,
#produces the exact self-contianed output foolder to be dropped onto a server (or a final container) and run as-is
# publish = package ethis app to actually ship it. Copied into final image below in next phase.
FROM build AS publish
#publish and build both compile. Publish was designed to run on its own. 
RUN dotnet publish "InventoryService.csproj" -c Release -o /app/publish



# new stage, back from small base runtime image
# not build or publish which still have bulky SDK.
# "throw away everything except what's needed" step
FROM base AS final
WORKDIR /app
# copies only compiled files into /app (just DLLs and config. No SDK, nor source code, no intermediate build artifacts)
COPY --from=publish /app/publish .
# command that runs when the container starts. "run this coompiled app using the runtime"
# final shipped image only has aspnet:8.0 runtime and compiled .dll output
ENTRYPOINT ["dotnet", "InventoryService.dll"]

























# IGNORE COMMENTED OUT CODE. KEEPING FOR EXPLANATION. COMMENTED OUT BECAUSE BUILD AND PUBLISH WERE REDUNDANT.
# compiles project, -c Release chooses Release config (optimized, vs. Debug). -o /app/build = output compiled files to this dir
#RUN dotnet build "InventoryService.csproj" -c Release -o /app/build
# publish compiles and produces DLLs for local running/debugging like build, but pusblish gathers
# everything for standard deployment: copies oover dependencies, trims/optimizes for a deployment target,
#produces the exact self-contianed output foolder to be dropped onto a server (or a final container) and run as-is
# publish = package ethis app to actually ship it. Copied into final image below in next phase.
#FROM build AS publish
#publish and build both compile. Publish was designed to run on its own. 
# commenting out build since it's compiling twice for no reason. 
#RUN dotnet publish "InventoryService.csproj" -c Release -o /app/publish