(function () {
    'use strict';

    angular
        .module('app')
        .factory('factory1', factory1);

    factory1.$inject = ['$http'];

    function factory1($http) {
        var service = {
            getData: getData
        };

        return service;

        function getData() { }
    }
})();