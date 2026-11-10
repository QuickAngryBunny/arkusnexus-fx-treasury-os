import type { ReactNode } from "react";
import { BrandMark, StrokeIcon, navIcons, type NavIcon } from "./icons";
import { cx, type LinkComponent } from "./util";

export type AppNavItem = { key: string; label: ReactNode; href: string; icon: NavIcon };

export type AppNavProps = {
  items: ReadonlyArray<AppNavItem>;
  /** Key of the current section; that item gets aria-current="page". */
  active?: string;
  brand: { title: ReactNode; subtitle?: ReactNode; href: string; label: string };
  /** Accessible name of the nav landmark. */
  label?: string;
  /** Optional plum progress card above the user menu (e.g. setup progress). */
  card?: { kicker: ReactNode; title: ReactNode; href: string; progress?: number };
  /** The <UserMenu/> (or any footer content). */
  user?: ReactNode;
  linkAs?: LinkComponent;
  className?: string;
};

/** Shared sidebar: brand, main sections, optional progress card, user menu. A top bar on narrow screens. */
export function AppNav({ items, active, brand, label = "Primary", card, user, linkAs: Link = "a", className }: AppNavProps) {
  return (
    <nav aria-label={label} className={cx("vi-nav", className)}>
      <Link href={brand.href} className="vi-nav__brand" aria-label={brand.label}>
        <BrandMark />
        <span className="vi-nav__brand-text">
          {brand.title}
          {brand.subtitle && <small>{brand.subtitle}</small>}
        </span>
      </Link>

      <ul className="vi-nav__items">
        {items.map((it) => (
          <li key={it.key}>
            <Link href={it.href} className="vi-nav__item" aria-current={it.key === active ? "page" : undefined}>
              <StrokeIcon d={navIcons[it.icon]} />
              {it.label}
            </Link>
          </li>
        ))}
      </ul>

      {(card || user) && (
        <div className="vi-nav__footer">
          {card && (
            <Link href={card.href} className="vi-nav__card">
              <span className="vi-nav__card-kicker">{card.kicker}</span>
              <span className="vi-nav__card-title">{card.title}</span>
              {card.progress !== undefined && (
                <span className="vi-nav__progress" aria-hidden="true">
                  <span style={{ width: `${Math.round(Math.min(1, Math.max(0, card.progress)) * 100)}%` }} />
                </span>
              )}
            </Link>
          )}
          {user}
        </div>
      )}
    </nav>
  );
}
