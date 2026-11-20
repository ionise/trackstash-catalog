using TrackStash.Catalog.Api;
using TrackStash.Catalog.Api.Authentication;

if (args is ["hash-admin-password", ..])
    return AdminPasswordCommand.Run(args);
var app = CatalogApiHost.Build(args);
await app.RunAsync();
return 0;
