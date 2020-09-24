import { Observable, Subject, BehaviorSubject } from 'rxjs';
import { InjectionToken } from '@angular/core';
import { scan } from 'rxjs/operators';
import { ApiUser, AuthUser, Home, DirectoryHome } from './models';

export interface ApplicationState {
    authUser: AuthUser,
    apiUser: ApiUser,
    directory: DirectoryHome[],
    operationsInProgress: number
}

export const initialStateValue: ApplicationState = {
    authUser: null,
    apiUser: null,
    directory: [],
    operationsInProgress: 0
}

export class AuthenticatedUserChanged { constructor(public authUser: AuthUser) { } }

export class LoadDirectory { }

export class LoadDirectoryCompleted { constructor(public data: DirectoryHome[]) { } }

export class LoadUser { }

export class LoadUserCompleted { constructor(public user: ApiUser) { } }

export class Login { }

export class Logout { }

export type Action =
    AuthenticatedUserChanged |
    LoadDirectory |
    LoadDirectoryCompleted |
    LoadUser |
    LoadUserCompleted |
    Login |
    Logout;

export const dispatcher = new InjectionToken<Subject<Action>>('dispatcher');

export const initialState = new InjectionToken<ApplicationState>('initialState');

export const applicationState = new InjectionToken<ApplicationState>('applicationState');

export function applicationStateFactory(initialState: ApplicationState, dispatcher: Observable<Action>): Observable<ApplicationState> {

    let appStateObservable = dispatcher.pipe(
        scan((state: ApplicationState, action: Action) => {

            console.log('Processing action ', action);

            let newState: ApplicationState;

            if (action instanceof AuthenticatedUserChanged) {
                newState = {
                    authUser: action.authUser,
                    apiUser: state.apiUser,
                    directory: state.directory,
                    operationsInProgress: state.operationsInProgress
                }
            } else if (action instanceof LoadDirectory) {
                newState = addOperationInProgress(state);
            } else if (action instanceof LoadDirectoryCompleted) {
                newState = {
                    authUser: state.authUser,
                    apiUser: state.apiUser,
                    directory: action.data,
                    operationsInProgress: state.operationsInProgress - 1
                }
            } else if (action instanceof LoadUser) {
                newState = addOperationInProgress(state);
            } else if (action instanceof LoadUserCompleted) {
                newState = {
                    authUser: state.authUser,
                    apiUser: action.user,
                    directory: state.directory,
                    operationsInProgress: state.operationsInProgress - 1
                }
            } else if (action instanceof Login) {
                newState = state;
            } else if (action instanceof Logout) {
                newState = state;
            }

            console.log('Emitting new state', newState);

            return newState;
        }, initialState));

    const behaviorSubject = new BehaviorSubject<ApplicationState>(initialState);
    appStateObservable.subscribe(s => behaviorSubject.next(s));
    return behaviorSubject;
}

function addOperationInProgress(state: ApplicationState) {
    return {
        authUser: state.authUser,
        apiUser: state.apiUser,
        directory: state.directory,
        operationsInProgress: state.operationsInProgress + 1
    };
}
