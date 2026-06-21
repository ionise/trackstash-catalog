class TrackStashCatalogEntityArtifact {
    [string]$Kind
    [string]$Name
    [string]$NormalizedName
    [string]$Slug
    [string]$Path
    [string]$Content

    TrackStashCatalogEntityArtifact([string]$kind, [string]$name, [string]$normalizedName, [string]$slug, [string]$path, [string]$content) {
        $this.Kind = $kind
        $this.Name = $name
        $this.NormalizedName = $normalizedName
        $this.Slug = $slug
        $this.Path = $path
        $this.Content = $content
    }
}