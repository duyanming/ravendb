﻿import viewModelBase = require("viewmodels/viewModelBase");
import shell = require("viewmodels/shell");
import database = require("models/database");

class backupDatabase extends viewModelBase {

    databaseName = ko.observable<string>('');
    backupLocation = ko.observable<string>('');
    backupStatusMessages = ko.observableArray<backupMessageDto>();
    isBusy = ko.observable<boolean>();
    databaseNames: KnockoutComputed<string[]>;
    searchResults: KnockoutComputed<string[]>;
    nameCustomValidity = ko.observable<string>('');

    constructor() {
        super();

        this.databaseNames = ko.computed(() => shell.databases().map((db: database) => db.name));

        this.searchResults = ko.computed(() => {
            var newDatabaseName = this.databaseName();
            if (!newDatabaseName || newDatabaseName.length < 2) {
                return [];
            }
            return this.databaseNames().filter((name) => name.toLowerCase().indexOf(newDatabaseName.toLowerCase()) > -1);
        });

        this.databaseName.subscribe((newDatabaseName: string) => {
            var errorMessage: string = '';
            var foundDb = shell.databases.first((db: database) => newDatabaseName == db.name);

            if (!foundDb && newDatabaseName.length > 0) {
                errorMessage = "Database name doesn't exist!";
            }

            this.nameCustomValidity(errorMessage);
        });
    }

    canActivate(args): any {
        return true;
    }

    compositionComplete() {
        super.compositionComplete();
        $('form :input[name="databaseName"]').on("keypress", (e) => e.which != 13);
    }

    startBackup() {
        this.isBusy(true);

        var updateBackupStatus = (newBackupStatus: backupStatusDto) => {
            this.backupStatusMessages(newBackupStatus.Messages);
            this.isBusy(!!newBackupStatus.IsRunning);
        };
        
        require(["commands/backupDatabaseCommand"], backupDatabaseCommand => {
            var dbToBackup = shell.databases.first((db: database) => db.name == this.databaseName());
            new backupDatabaseCommand(dbToBackup, this.backupLocation(), updateBackupStatus)
                .execute()
                .always(() => this.isBusy(false));
        });
    }
}

export = backupDatabase;