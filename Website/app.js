const LISTING_URL = "{{ listingInfo.Url }}";

const PACKAGE_DESCRIPTIONS_JA = {
  "com.chanya.ani-cursor": "Windowsのアニメーションカーソル（.ani）から、VRChat / Modular Avatar対応の立体カーソルPrefabを生成するツールです。",
};

const PACKAGE_TYPES_JA = {
  Any: "共通",
  Avatar: "アバター",
  Tool: "ツール",
  World: "ワールド",
  tool: "ツール",
};

const PACKAGES = {
{{~ for package in packages ~}}
  "{{ package.Name }}": {
    name: "{{ package.Name }}",
    displayName: "{{ if package.DisplayName; package.DisplayName; end; }}",
    description: PACKAGE_DESCRIPTIONS_JA["{{ package.Name }}"] ?? "{{ if package.Description; package.Description; end; }}",
    version: "{{ package.Version }}",
    author: {
      name: "{{ if package.Author.Name; package.Author.Name; end; }}",
      url: "{{ if package.Author.Url; package.Author.Url; end; }}",
    },
    dependencies: {
      {{~ for dependency in package.Dependencies ~}}
        "{{ dependency.Name }}": "{{ dependency.Version }}",
      {{~ end ~}}
    },
    keywords: [
      {{~ for keyword in package.Keywords ~}}
        "{{ keyword }}",
      {{~ end ~}}
    ],
    license: "{{ package.License }}",
    licensesUrl: "{{ package.LicenseUrl }}",
  },
{{~ end ~}}
};

const packageGrid = document.getElementById("packageGrid");
const searchInput = document.getElementById("searchInput");
const emptyState = document.getElementById("emptyState");
const searchStatus = document.getElementById("searchStatus");
const copyStatus = document.getElementById("copyStatus");
const helpDialog = document.getElementById("addListingToVccHelp");
const packageDialog = document.getElementById("packageInfoModal");

const openVccListing = () => {
  window.location.assign(`vcc://vpm/addRepo?url=${encodeURIComponent(LISTING_URL)}`);
};

const showDialog = (dialog, focusTarget) => {
  if (!dialog || dialog.open) return;
  const openDialog = document.querySelector("dialog[open]");
  if (openDialog) openDialog.close();
  dialog.showModal();
  requestAnimationFrame(() => focusTarget?.focus({ preventScroll: true }));
};

const closeOnBackdrop = dialog => {
  dialog?.addEventListener("click", event => {
    if (event.target === dialog) dialog.close();
  });
};

const writeClipboard = async value => {
  if (navigator.clipboard && window.isSecureContext) {
    await navigator.clipboard.writeText(value);
    return;
  }

  const fallback = document.createElement("textarea");
  fallback.value = value;
  fallback.setAttribute("readonly", "");
  fallback.className = "clipboard-fallback";
  document.body.appendChild(fallback);
  fallback.select();
  const copied = document.execCommand("copy");
  fallback.remove();
  if (!copied) throw new Error("Clipboard copy failed");
};

const setCopyFeedback = (button, state) => {
  const label = button.querySelector(".copy-button__label");
  const succeeded = state === "success";
  button.dataset.state = state;
  if (label) label.textContent = succeeded ? "コピー済み" : "コピー失敗";
  copyStatus.textContent = succeeded ? "パッケージリストURLをコピーしました。" : "URLをコピーできませんでした。手動で選択してください。";

  window.setTimeout(() => {
    delete button.dataset.state;
    if (label) label.textContent = "コピー";
  }, 2500);
};

document.querySelectorAll("[data-add-vcc]").forEach(button => {
  button.addEventListener("click", openVccListing);
});
document.getElementById("vccAddRepoButton")?.addEventListener("click", openVccListing);
document.querySelectorAll(".rowAddToVccButton").forEach(button => {
  button.addEventListener("click", openVccListing);
});

document.querySelectorAll("[data-copy-target]").forEach(button => {
  button.addEventListener("click", async () => {
    const input = document.getElementById(button.dataset.copyTarget);
    if (!input) return;
    try {
      await writeClipboard(input.value);
      setCopyFeedback(button, "success");
    } catch (error) {
      input.select();
      setCopyFeedback(button, "error");
      console.error("パッケージリストURLをコピーできませんでした。", error);
    }
  });
});

packageGrid.querySelectorAll("[data-package-row]").forEach(row => {
  const packageInfo = PACKAGES[row.dataset.packageId];
  const description = row.querySelector(".packageDescription");
  const type = row.querySelector("[data-package-type]");
  if (packageInfo?.description && description) description.textContent = packageInfo.description;
  if (type) type.textContent = PACKAGE_TYPES_JA[type.dataset.packageType] ?? type.dataset.packageType;
});

let searchTimer;
searchInput.addEventListener("input", () => {
  window.clearTimeout(searchTimer);
  searchTimer = window.setTimeout(() => {
    const query = searchInput.value.trim().toLocaleLowerCase("ja");
    const rows = [...packageGrid.querySelectorAll("[data-package-row]")];
    let visibleCount = 0;

    rows.forEach(row => {
      const name = row.dataset.packageName?.toLocaleLowerCase("ja") ?? "";
      const id = row.dataset.packageId?.toLocaleLowerCase("ja") ?? "";
      const visible = query === "" || name.includes(query) || id.includes(query);
      row.hidden = !visible;
      if (visible) visibleCount += 1;
    });

    emptyState.hidden = visibleCount !== 0;
    searchStatus.textContent = `${visibleCount}件のパッケージを表示しています。`;
  }, 250);
});

document.getElementById("urlBarHelp")?.addEventListener("click", () => {
  showDialog(helpDialog, document.getElementById("addListingToVccHelpClose"));
});
document.getElementById("addListingToVccHelpClose")?.addEventListener("click", () => helpDialog.close());
document.getElementById("packageInfoModalClose")?.addEventListener("click", () => packageDialog.close());
document.getElementById("packageInfoListingHelp")?.addEventListener("click", () => {
  packageDialog.close();
  showDialog(helpDialog, document.getElementById("addListingToVccHelpClose"));
});
closeOnBackdrop(helpDialog);
closeOnBackdrop(packageDialog);

const packageInfoName = document.getElementById("packageInfoName");
const packageInfoId = document.getElementById("packageInfoId");
const packageInfoVersion = document.getElementById("packageInfoVersion");
const packageInfoDescription = document.getElementById("packageInfoDescription");
const packageInfoAuthor = document.getElementById("packageInfoAuthor");
const packageInfoDependencies = document.getElementById("packageInfoDependencies");
const packageInfoDependenciesGroup = document.getElementById("packageInfoDependenciesGroup");
const packageInfoKeywords = document.getElementById("packageInfoKeywords");
const packageInfoKeywordsGroup = document.getElementById("packageInfoKeywordsGroup");
const packageInfoLicense = document.getElementById("packageInfoLicense");
const packageInfoLicenseGroup = document.getElementById("packageInfoLicenseGroup");

document.querySelectorAll(".rowPackageInfoButton").forEach(button => {
  button.addEventListener("click", event => {
    const packageId = event.currentTarget.dataset.packageId;
    const packageInfo = PACKAGES[packageId];
    if (!packageInfo) {
      console.error(`パッケージ ${packageId} が見つかりません。`, PACKAGES);
      return;
    }

    packageInfoName.textContent = packageInfo.displayName;
    packageInfoId.textContent = packageId;
    packageInfoVersion.textContent = `v${packageInfo.version}`;
    packageInfoDescription.textContent = packageInfo.description;
    packageInfoAuthor.textContent = packageInfo.author.name;
    packageInfoAuthor.href = packageInfo.author.url;

    const dependencies = Object.entries(packageInfo.dependencies);
    packageInfoDependencies.replaceChildren();
    packageInfoDependenciesGroup.hidden = dependencies.length === 0;
    dependencies.forEach(([name, version]) => {
      const item = document.createElement("li");
      const packageName = document.createElement("code");
      packageName.textContent = name;
      const packageVersion = document.createElement("span");
      packageVersion.textContent = `v${version}`;
      item.append(packageName, packageVersion);
      packageInfoDependencies.appendChild(item);
    });

    packageInfoKeywords.replaceChildren();
    packageInfoKeywordsGroup.hidden = packageInfo.keywords.length === 0;
    packageInfo.keywords.forEach(keyword => {
      const badge = document.createElement("span");
      badge.className = "keyword";
      badge.textContent = keyword;
      packageInfoKeywords.appendChild(badge);
    });

    const hasLicense = Boolean(packageInfo.license || packageInfo.licensesUrl);
    packageInfoLicenseGroup.hidden = !hasLicense;
    packageInfoLicense.textContent = packageInfo.license || "ライセンスを確認";
    packageInfoLicense.href = packageInfo.licensesUrl || "https://github.com/ChanyaVRC/AniCursor/blob/main/LICENSE.md";

    showDialog(packageDialog, document.getElementById("packageInfoModalClose"));
  });
});
