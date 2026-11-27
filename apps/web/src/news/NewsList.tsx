import { formatAge } from "@marketlens/shared/format";
import type { UseQueryResult } from "@tanstack/react-query";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import type { News, NewsArticle } from "../api/client";
import { useNow } from "../components/useNow";
import { LoadError } from "../components/States";
import { useSettings } from "../settings/Settings";

/**
 * A list of articles from the news API (D27): picture (when the publisher has one), headline linking
 * to the publisher in a new tab, source and age, and a short summary. Handles loading, a failed load,
 * no key, Finnhub being unavailable and an empty list. Pictures load lazily and without a referrer,
 * so publishers aren't told which Marketlens page their reader came from.
 */
export function NewsList({
  news,
  emptyText,
  rows,
}: {
  news: UseQueryResult<News>;
  emptyText: string;
  /** How many placeholder rows to show while loading, matching the list that will replace them. */
  rows: number;
}) {
  const { t } = useTranslation();

  if (news.isPending) {
    return <NewsSkeleton rows={rows} />;
  }

  if (news.isError) {
    return <LoadError error={news.error} onRetry={() => void news.refetch()} />;
  }

  const { status, items } = news.data;
  if (status === "no-provider-key") {
    return <p className="py-4 text-small text-muted">{t("news.noProviderKey")}</p>;
  }

  if (status === "unavailable") {
    return <p className="py-4 text-small text-muted">{t("news.unavailable")}</p>;
  }

  if (items.length === 0) {
    return <p className="py-4 text-small text-muted">{emptyText}</p>;
  }

  return (
    <ul className="divide-y divide-divider">
      {items.map((article) => (
        <li key={article.id}>
          <ArticleRow article={article} />
        </li>
      ))}
    </ul>
  );
}

function ArticleRow({ article }: { article: NewsArticle }) {
  const { t } = useTranslation();
  // A picture that fails to load (moved, blocked) disappears instead of leaving a broken frame.
  const [hasImage, setHasImage] = useState(article.imageUrl !== null);

  return (
    <article className="flex gap-4 py-4">
      {hasImage && article.imageUrl && (
        <img
          src={article.imageUrl}
          alt=""
          loading="lazy"
          decoding="async"
          referrerPolicy="no-referrer"
          onError={() => {
            setHasImage(false);
          }}
          className="h-16 w-24 shrink-0 rounded-control bg-background object-cover sm:h-20 sm:w-32"
        />
      )}
      <div className="min-w-0">
        <h3 className="text-body font-semibold leading-snug">
          <a
            href={article.url}
            target="_blank"
            rel="noopener noreferrer"
            className="break-words hover:underline focus-visible:underline"
          >
            {article.headline}
            <span className="sr-only"> {t("news.opensPublisher", { source: article.source })}</span>
          </a>
        </h3>
        <p className="mt-1 text-caption text-muted">
          <ArticleMeta source={article.source} publishedAtUtc={article.publishedAtUtc} />
        </p>
        {article.summary && (
          <p className="mt-1 line-clamp-2 break-words text-small text-muted">{article.summary}</p>
        )}
      </div>
    </article>
  );
}

/** "Reuters · 3 hr. ago", ticking with the shared clock; its own component so only it re-renders. */
function ArticleMeta({ source, publishedAtUtc }: { source: string; publishedAtUtc: string }) {
  const { t } = useTranslation();
  const { language } = useSettings();
  const now = useNow();

  return (
    <time dateTime={publishedAtUtc}>
      {t("news.meta", { source, age: formatAge(publishedAtUtc, now, language) })}
    </time>
  );
}

/** Placeholder rows shaped like articles, so the list doesn't jump when it arrives. */
function NewsSkeleton({ rows }: { rows: number }) {
  return (
    <div aria-hidden className="divide-y divide-divider">
      {Array.from({ length: rows }, (_, row) => (
        <div key={row} className="flex gap-4 py-4">
          <span className="h-16 w-24 shrink-0 animate-pulse rounded-control bg-background sm:h-20 sm:w-32" />
          <span className="flex-1 space-y-2">
            <span className="block h-4 w-4/5 animate-pulse rounded-control bg-background" />
            <span className="block h-3 w-1/3 animate-pulse rounded-control bg-background" />
            <span className="block h-3 w-full animate-pulse rounded-control bg-background" />
          </span>
        </div>
      ))}
    </div>
  );
}
