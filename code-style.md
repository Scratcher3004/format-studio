# Code Style guidelines

Most code style is fine, as long as its readable and somewhat consistent with what alread exists.
However, a few things should be noted:

1. When passing blazor parameters, always use quotes and at-symbols. This removes potential ambiguity.
   e.g. `<MudText Typo="@Typo.body2">Text</MudText>`
