using TrackStash.Catalog.Api;

var app = CatalogApiHost.Build(args);
await app.RunAsync();
