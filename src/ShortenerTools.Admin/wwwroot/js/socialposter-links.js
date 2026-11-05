// SocialPoster's public saved-draft API is shared by its embeddable composer.
// Append links without discarding existing text, overrides or platform selections.
window.shortenerComposeLink = function (url, title, message) {
    const saved = window.socialPoster.loadDraft();
    // This action uses an existing record rather than creating a new link on submission.
    const draft = saved ? JSON.parse(saved) : {Base: "", Overrides: {}, Selected: {}};
    draft.SourceUrl = "";
    draft.WorkflowState = null;
    const append = text => {
        const value = text || "";
        if (value.split(/\s+/).includes(url)) return value;
        return value + (value && !value.endsWith("\n") ? "\n" : "") + url;
    };
    if (!draft.Base)
        draft.Base = [title, message].filter(value => value && value.trim()).join("\n\n");
    draft.Base = append(draft.Base);
    for (const field of ["Overrides", "CustomizedTexts"]) {
        for (const platform of Object.keys(draft[field] || {})) {
            if (draft[field][platform] != null)
                draft[field][platform] = append(draft[field][platform]);
        }
    }
    const json = JSON.stringify(draft);
    window.socialPoster.saveDraft(json);
    // The component's API swallows storage failures; keep the links UI open if saving failed.
    if (window.socialPoster.loadDraft() !== json)
        throw new Error("The SocialPoster draft could not be saved.");
};
