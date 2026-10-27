"""MSBuild project-file change analysis for the Dependency Delta scanner.

A .csproj change that touches only metadata elements, such as the project
version or the assembly name, does not alter the resolved package graph.
Such a change does not need a locked-mode restore to prove reproducibility,
so the csharp dependency scan can be skipped for it.

Deciding that requires knowing which element actually changed. A csproj is
frequently written as a single line, so the element has to be located from
the part of the line that differs rather than from the first tag it
happens to contain.
"""

from __future__ import annotations

import re

# MSBuild elements whose change does not alter the resolved package graph.
# Bumping <Version>, renaming an assembly, or editing a description does not
# require a `--locked-mode` restore to prove reproducibility, so changes that
# touch ONLY these elements are exempted from the csharp dependency scan.
METADATA_ONLY_CSPROJ_ELEMENTS: frozenset[str] = frozenset(
    {
        "Version",
        "AssemblyName",
        "RootNamespace",
        "Description",
        "Authors",
        "Company",
        "Copyright",
        "PackageId",
        "Title",
        "Summary",
        "Product",
        "InformationalVersion",
        "FileVersion",
        "AssemblyVersion",
        "NeutralLanguage",
    }
)

_OPEN_TAG = re.compile(r"<\s*(?P<tag>[A-Za-z][A-Za-z0-9_]*)\b")


def _enclosing_tag(text: str, position: int) -> str | None:
    """Return the name of the element enclosing *position* within *text*."""
    open_at = text.rfind("<", 0, position)
    if open_at < 0:
        return None
    match = _OPEN_TAG.match(text, open_at)
    return match.group("tag") if match else None


def _all_tags(text: str) -> set[str]:
    """Return every element name appearing in *text*."""
    return {match.group("tag") for match in _OPEN_TAG.finditer(text)}


def _element_spans(text: str) -> list[tuple[str, int, int]]:
    """Return the ``(tag, start, end)`` span of every LEAF element in *text*.

    The span covers the element's own text, from the character after its
    opening tag up to the character before its closing tag. Only elements
    whose body is plain text are returned: a container element's body
    changes whenever one of its children changes, so comparing containers
    would report a change for every edit. The innermost element that
    actually holds the changed text is the one that names the change.
    """
    spans: list[tuple[str, int, int]] = []
    for match in _OPEN_TAG.finditer(text):
        tag = match.group("tag")
        open_end = text.find(">", match.end())
        if open_end < 0:
            continue
        closing = f"</{tag}"
        close = text.find(closing, open_end)
        if close < 0:
            # A self-closing element holds its value in its attributes, so
            # the whole tag is compared. The "/>" marker sits immediately
            # before the ">" that ends the tag, so the search starts from
            # the beginning of the tag rather than from its end.
            tag_end = text.rfind("/>", match.start(), open_end)
            end = tag_end + 2 if tag_end >= 0 else open_end + 1
            spans.append((tag, match.start(), end))
            continue
        body = text[open_end + 1 : close]
        if "<" in body:
            # The element contains child markup, so it is a container.
            continue
        spans.append((tag, open_end + 1, close))
    return spans


def _changed_element_tags(removed: str, added: str) -> set[str]:
    """Return the element names whose content differs between *removed* and *added*.

    The two lines are compared element by element. Matching whole lines on
    tag names alone cannot tell a ``<Version>`` bump from a
    ``<PackageReference>`` bump when both live on the same single-line
    csproj document.

    Every differing element is reported, not just the first one. A line
    that changes both a metadata element and a package reference must
    report both, otherwise the package change would be classified as a
    metadata-only change and the dependency scan would be skipped.

    Elements are matched by position within the line, not by tag name,
    because the same tag can legitimately appear more than once in a
    document. Matching by name would let a later element's text be
    compared against an earlier one and report a spurious change.
    """
    if removed == added:
        return set()
    removed_spans = _element_spans(removed)
    added_spans = _element_spans(added)
    changed: set[str] = set()
    for index in range(max(len(removed_spans), len(added_spans))):
        old = removed_spans[index] if index < len(removed_spans) else None
        new = added_spans[index] if index < len(added_spans) else None
        if old is None or new is None:
            # An element was added or removed, so its tag changed.
            tag = (new or old)[0]
            changed.add(tag)
            continue
        old_tag, old_start, old_end = old
        new_tag, new_start, new_end = new
        if old_tag != new_tag or removed[old_start:old_end] != added[new_start:new_end]:
            changed.add(old_tag)
            changed.add(new_tag)
    if changed:
        return changed
    # The documents have no comparable element structure, so fall back to
    # locating the first difference and naming the element that encloses it.
    limit = min(len(removed), len(added))
    prefix = 0
    while prefix < limit and removed[prefix] == added[prefix]:
        prefix += 1
    suffix = 0
    while (
        suffix < (limit - prefix)
        and removed[len(removed) - 1 - suffix] == added[len(added) - 1 - suffix]
    ):
        suffix += 1
    if prefix >= len(removed) - suffix:
        tag = _enclosing_tag(added, prefix)
        return {tag} if tag else set()
    tag = _enclosing_tag(removed, prefix)
    return {tag} if tag else set()


def _changed_lines(diff_text: str) -> tuple[list[str], list[str]]:
    """Split *diff_text* into its removed and added content lines."""
    removed: list[str] = []
    added: list[str] = []
    for line in diff_text.splitlines():
        if line.startswith(("---", "+++")):
            continue
        if line.startswith("-"):
            removed.append(line[1:])
        elif line.startswith("+"):
            added.append(line[1:])
    return removed, added


def is_metadata_only_csproj_diff(diff_text: str) -> bool:
    """Return True iff every changed element in *diff_text* is an MSBuild metadata element.

    A diff that adds/removes/modifies only <Version>, <AssemblyName>, etc.
    does not change the resolved package graph, so the csharp lockfile scan
    is unnecessary. Any other touched element (PackageReference, Reference,
    ProjectReference, Import, etc.) forces a full scan.

    The changed element is identified by locating the part of the line that
    actually differs, so a single-line csproj is judged by the element that
    moved rather than by whichever tag happens to appear first.
    """
    removed, added = _changed_lines(diff_text)
    if not removed and not added:
        return True
    changed: set[str] = set()
    paired = min(len(removed), len(added))
    for index in range(paired):
        changed |= _changed_element_tags(removed[index], added[index])
    for line in removed[paired:]:
        changed |= _all_tags(line)
    for line in added[paired:]:
        changed |= _all_tags(line)
    if not changed:
        return True
    return all(tag in METADATA_ONLY_CSPROJ_ELEMENTS for tag in changed)
