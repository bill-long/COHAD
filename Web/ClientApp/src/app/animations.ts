import { trigger, transition, style, query, animateChild, group, animate } from '@angular/animations';

export const slideInAnimation =
  trigger('routeAnimations', [
    transition('HomePage <=> AboutPage', [
      style({ position: 'relative' }),
      query(':enter, :leave', [
        style({
          position: 'absolute',
          top: 0,
          left: 0,
          width: '100%',
          height: '100%'
        })
      ]),
      query(':enter', [
        style({ left: '-100%' })
      ]),
      query(':leave', animateChild()),
      group([
        query(':leave', [
          animate('300ms ease-out', style({ left: '100%' }))
        ]),
        query(':enter', [
          animate('300ms ease-out', style({ left: '0%' }))
        ])
      ]),
      query(':enter', animateChild()),
    ]),
    transition('* <=> NewsPage', [
      style({ position: 'relative' }),
      query(':enter, :leave', [
        style({
          position: 'absolute',
          top: 0,
          left: 0,
          width: '100%'
        })
      ]),
      query(':enter', [
        style({ left: '-100%' })
      ]),
      query(':leave', animateChild()),
      group([
        query(':leave', [
          animate('200ms ease-out', style({ left: '100%' }))
        ]),
        query(':enter', [
          animate('300ms ease-out', style({ left: '0%' }))
        ])
      ]),
      query(':enter', animateChild()),
    ])
  ]);

export const fadeInAnimation =
  trigger('routeAnimations', [
    transition('* <=> *', [
      style({ position: 'relative' }),
      query(':enter, :leave', [
        style({
          position: 'absolute',
          top: 0,
          left: 0,
          width: '100%',
          height: '100%'
        })
      ],
        { optional: true }),
      query(':enter .header', [
        style({ opacity: 0 })
      ],
        { optional: true }),
      query(':enter .container', [
        style({ opacity: 0 })
      ],
        { optional: true }),
      query(':leave .header', [
        style({ opacity: 1 })
      ],
        { optional: true }),
      query(':leave .container', [
        style({ opacity: 1 })
      ],
        { optional: true }),
      query(':leave', animateChild(), { optional: true }),
      group([
        query(':leave .header', [
          animate('300ms ease-out', style({ opacity: 0 })),
        ],
          { optional: true }),
        query(':leave .container', [
          animate('300ms ease-out', style({ opacity: 0 }))
        ],
          { optional: true }),
        query(':enter .header', [
          animate('300ms ease-out', style({ opacity: 1 }))
        ],
          { optional: true }),
        query(':enter .container', [
          animate('300ms ease-out', style({ opacity: 1 }))
        ],
          { optional: true })
      ]),
      query(':enter', animateChild(), { optional: true }),
    ])
  ]);