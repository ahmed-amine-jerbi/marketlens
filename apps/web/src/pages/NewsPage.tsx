import { useState, type KeyboardEvent } from "react";
import { useTranslation } from "react-i18next";

import { PageTitle } from "../app/Layout";
import { useNews } from "../api/queries";
import { rovingIndex } from "../components/rovingFocus";
import { NewsList } from "../news/NewsList";

const topics = [
  { id: "markets", label: "news.markets" },
  { id: "crypto", label: "news.crypto" },
] as const;
const ARTICLES_SHOWN = 30;

/** Stock-market and crypto headlines (D27), one topic at a time. */
export function NewsPage() {
  const { t } = useTranslation();
  const [topicId, setTopicId] = useState<(typeof topics)[number]["id"]>("markets");
  const news = useNews(topicId, ARTICLES_SHOWN);

  // Arrow keys move between tabs and select them (WAI-ARIA tabs with automatic activation).
  const selectByKey = (event: KeyboardEvent<HTMLDivElement>) => {
    const index = topics.findIndex((topic) => topic.id === topicId);
    const nextIndex = rovingIndex(event.key, index, topics.length);
    const next = nextIndex === undefined ? undefined : topics[nextIndex];
    if (!next) {
      return;
    }

    event.preventDefault();
    setTopicId(next.id);
    document.getElementById(`news-tab-${next.id}`)?.focus();
  };

  return (
    <div className="max-w-3xl">
      <PageTitle title={t("news.title")} />
      <h1 className="text-heading font-semibold">{t("news.title")}</h1>
      <p className="mt-1 max-w-prose text-small text-muted">{t("news.intro")}</p>

      <div
        role="tablist"
        aria-label={t("news.tabsLabel")}
        onKeyDown={selectByKey}
        className="mt-6 flex gap-1"
      >
        {topics.map((topic) => (
          <button
            key={topic.id}
            id={`news-tab-${topic.id}`}
            type="button"
            role="tab"
            aria-selected={topic.id === topicId}
            aria-controls="news-panel"
            tabIndex={topic.id === topicId ? 0 : -1}
            onClick={() => {
              setTopicId(topic.id);
            }}
            className={`min-h-11 rounded-control px-3 py-1.5 text-small ${topic.id === topicId ? "bg-surface font-semibold" : "text-muted hover:text-ink"}`}
          >
            {t(topic.label)}
          </button>
        ))}
      </div>
      <div
        id="news-panel"
        role="tabpanel"
        aria-labelledby={`news-tab-${topicId}`}
        className="mt-2 rounded-panel bg-surface px-4"
      >
        <NewsList news={news} emptyText={t("news.empty")} rows={6} />
      </div>
      <p className="mt-4 text-caption text-muted">{t("news.notice")}</p>
    </div>
  );
}
