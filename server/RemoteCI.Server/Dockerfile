# 多阶段构建：SDK 阶段编译，运行时阶段仅带 aspnet 运行时，适合 NAS/轻量服务器。
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY shared/RemoteCI.Shared/ shared/RemoteCI.Shared/
COPY server/RemoteCI.Server/ server/RemoteCI.Server/
RUN dotnet publish server/RemoteCI.Server/RemoteCI.Server.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

EXPOSE 8080
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
ENV Server__DatabasePath=/app/data/remoteci.db
VOLUME ["/app/data"]
ENTRYPOINT ["dotnet", "RemoteCI.Server.dll"]
