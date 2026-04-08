$version = ( [xml]( Get-Content "DeerCrypt/DeerCrypt.csproj" ) ).Project.PropertyGroup.Version
$tag = "v$version"

if( git tag --list $tag )
{
    Write-Error "Tag $tag already exists. Bump the version first."
    exit 1
}

git tag $tag
git push origin $tag
Write-Host "Tagged and pushed $tag"